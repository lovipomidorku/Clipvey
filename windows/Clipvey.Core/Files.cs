using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Clipvey.Core;

// Файлы и папки по docs/protocol.md, «Файлы»: описание, проверки, двоичные куски, имена на диске.
// Совпадает с Files.swift и FileTree.swift на Mac; проверяется Clipvey.Tests по tests/vectors.json.

/// Элемент описания: файл (Size ≥ 0) или папка (Size = null). Path — относительный, части через «/».
public sealed record FileItem(string Path, long? Size)
{
    public bool IsDirectory => Size is null;

    public static FileItem File(string path, long size) => new(path, size);

    public static FileItem Directory(string path) => new(path, null);
}

/// Описание скопированных файлов и папок (file_offer). Items — в порядке обхода, папка раньше своего содержимого.
public sealed record FileOffer(string Id, IReadOnlyList<FileItem> Items, long Total)
{
    /// Сколько в описании файлов (без папок).
    public int FileCount => Items.Count(item => !item.IsDirectory);

    /// Элементы верхнего уровня — то, что скопировал пользователь.
    public IEnumerable<FileItem> TopLevel => Items.Where(item => !item.Path.Contains('/'));

    /// Почему описание нельзя принять (для журнала); null — можно. Правила — docs/protocol.md, «Описание».
    public static string? Refusal(string id, IReadOnlyList<FileItem> items, long total)
    {
        if (!IsValidId(id))
            return "id — не 32 шестнадцатеричных символа";
        if (items.Count == 0)
            return "пустой список элементов";
        if (items.Count > Protocol.MaxFileItems)
            return $"элементов больше {Protocol.MaxFileItems}";
        // Путь → папка ли это.
        var seen = new Dictionary<string, bool>(items.Count, StringComparer.Ordinal);
        long sum = 0;
        foreach (var item in items)
        {
            if (FileNames.PathProblem(item.Path) is { } problem)
                return $"путь «{item.Path}»: {problem}";
            if (seen.ContainsKey(item.Path))
                return $"путь «{item.Path}» повторяется";
            var slash = item.Path.LastIndexOf('/');
            if (slash >= 0 && !(seen.TryGetValue(item.Path[..slash], out var parentIsDirectory) && parentIsDirectory))
                return $"у «{item.Path}» нет родительской папки выше по списку";
            if (item.Size is { } size)
            {
                if (size < 0)
                    return $"размер «{item.Path}» меньше нуля";
                sum += size;
                if (sum > Protocol.MaxFileTotalBytes)
                    return "всего больше 10 ГиБ";
            }
            seen[item.Path] = item.IsDirectory;
        }
        return total == sum ? null : $"total {total} не равен сумме размеров {sum}";
    }

    /// id — 16 байт в hex строчными (32 символа): он становится именем папки (Incoming/&lt;id&gt;).
    public static bool IsValidId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// Разбор file_offer с проверками. Ошибка — FileMessageException: описание пропускается целиком, сеанс продолжается.
    public static FileOffer Parse(JsonObject message)
    {
        var id = Messages.OptionalString(message, "id") ?? throw new FileMessageException("нет id");
        if (message["items"] is not JsonArray array)
            throw new FileMessageException("нет items");
        if (array.Count > Protocol.MaxFileItems)
            throw new FileMessageException($"элементов больше {Protocol.MaxFileItems}");
        var items = new List<FileItem>(array.Count);
        foreach (var node in array)
        {
            if (node is not JsonObject entry || Messages.OptionalString(entry, "path") is not { } path)
                throw new FileMessageException("элемент без пути");
            if (entry["dir"] is JsonValue dir && dir.TryGetValue(out bool isDirectory) && isDirectory)
            {
                if (entry.ContainsKey("size"))
                    throw new FileMessageException($"у папки «{path}» есть size");
                items.Add(FileItem.Directory(path));
            }
            else
            {
                if (Messages.OptionalInteger(entry, "size") is not (>= 0 and var size))
                    throw new FileMessageException($"у файла «{path}» нет целого size ≥ 0");
                items.Add(FileItem.File(path, size));
            }
        }
        var total = Messages.OptionalInteger(message, "total") ?? throw new FileMessageException("нет целого total");
        if (Refusal(id, items, total) is { } refusal)
            throw new FileMessageException(refusal);
        return new FileOffer(id, items, total);
    }

    public JsonObject ToMessage()
    {
        var items = new JsonArray();
        foreach (var item in Items)
        {
            items.Add(item.Size is { } size
                ? new JsonObject { ["path"] = item.Path, ["size"] = size }
                : new JsonObject { ["path"] = item.Path, ["dir"] = true });
        }
        return new JsonObject { ["t"] = "file_offer", ["id"] = Id, ["items"] = items, ["total"] = Total };
    }
}

/// Сообщения file_get, file_end, file_error и file_cancel.
public static class FileMessages
{
    public static JsonObject Get(string id, uint req, int index, long offset) => new()
    {
        ["t"] = "file_get",
        ["id"] = id,
        ["req"] = req,
        ["index"] = index,
        ["offset"] = offset,
    };

    public static JsonObject End(uint req, long size) => new() { ["t"] = "file_end", ["req"] = req, ["size"] = size };

    public static JsonObject Error(uint req, string reason) => new() { ["t"] = "file_error", ["req"] = req, ["reason"] = reason };

    public static JsonObject Cancel(uint req) => new() { ["t"] = "file_cancel", ["req"] = req };

    /// req: целое от 1 до 4 294 967 295, иначе FileMessageException (сообщение пропускается).
    public static uint Req(JsonObject message) =>
        Messages.OptionalInteger(message, "req") is >= 1 and <= uint.MaxValue and var req
            ? (uint)req
            : throw new FileMessageException("req не целое от 1 до 4294967295");

    /// file_get: id, index и offset; index и offset — −1, если поля нет или оно неверное (ответ not_found).
    public static (string Id, int Index, long Offset) ParseGet(JsonObject message) => (
        Messages.OptionalString(message, "id") ?? "",
        Messages.OptionalInteger(message, "index") is >= 0 and <= int.MaxValue and var index ? (int)index : -1,
        Messages.OptionalInteger(message, "offset") is >= 0 and var offset ? offset : -1);

    /// file_end: size — целое ≥ 0.
    public static long ParseEndSize(JsonObject message) =>
        Messages.OptionalInteger(message, "size") is >= 0 and var size ? size : throw new FileMessageException("size не целое ≥ 0");

    /// file_error: reason; нет — unavailable.
    public static string ParseErrorReason(JsonObject message) => Messages.OptionalString(message, "reason") ?? "unavailable";
}

/// Двоичный кусок файла: открытый текст шифрованного кадра `00 ‖ big-endian uint32 req ‖ данные`.
public static class FileChunk
{
    /// Байт 0x00 и req.
    public const int HeaderBytes = 5;

    public static void WriteHeader(Span<byte> destination, uint req)
    {
        destination[0] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], req);
    }

    /// Открытый текст куска целиком (с копированием данных; для проверок).
    public static byte[] Plaintext(uint req, ReadOnlySpan<byte> data)
    {
        var result = new byte[HeaderBytes + data.Length];
        WriteHeader(result, req);
        data.CopyTo(result.AsSpan(HeaderBytes));
        return result;
    }

    /// Разбор открытого текста, который начинается с 0x00: req и длина данных (данные — с HeaderBytes).
    /// Ошибка — FileMessageException: кадр пропускается, сеанс продолжается.
    public static (uint Req, int DataLength) Parse(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length <= HeaderBytes || plaintext[0] != 0)
            throw new FileMessageException($"двоичный кадр короче {HeaderBytes + 1} байт");
        var length = plaintext.Length - HeaderBytes;
        if (length > Protocol.FileChunkBytes)
            throw new FileMessageException($"кусок {length} байт больше 1 МиБ");
        return (BinaryPrimitives.ReadUInt32BigEndian(plaintext[1..]), length);
    }
}

/// Имена файлов: что отправлять в описании и как назвать полученное на диске.
/// Не часть протокола (docs/protocol.md, «Поведение сторон»), но одинаково на Mac и Windows.
public static class FileNames
{
    private static readonly HashSet<string> ReservedWindowsNames = ["CON", "PRN", "AUX", "NUL"];

    /// Что не так с путём из описания; null — всё в порядке.
    public static string? PathProblem(string path)
    {
        if (Encoding.UTF8.GetByteCount(path) > Protocol.MaxFilePathBytes)
            return $"длиннее {Protocol.MaxFilePathBytes} байт";
        foreach (var part in path.Split('/'))
        {
            if (NameProblem(part) is { } problem)
                return problem;
        }
        return null;
    }

    /// Что не так с частью пути; null — всё в порядке.
    public static string? NameProblem(string name)
    {
        if (name.Length == 0)
            return "пустая часть";
        if (name is "." or "..")
            return $"часть «{name}»";
        if (Encoding.UTF8.GetByteCount(name) > Protocol.MaxFileNameBytes)
            return $"часть длиннее {Protocol.MaxFileNameBytes} байт";
        if (name.AsSpan().IndexOfAny("/\\:\0") >= 0)
            return $"недопустимый символ в «{name}»";
        return null;
    }

    /// Имя для описания: NFC, «\», «:», «/» и символ 0 — на «_».
    public static string WireName(string name)
    {
        string normalized;
        try
        {
            normalized = name.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            normalized = name;
        }
        if (normalized.AsSpan().IndexOfAny("/\\:\0") < 0)
            return normalized;
        var builder = new StringBuilder(normalized);
        for (var i = 0; i < builder.Length; i++)
        {
            if (builder[i] is '/' or '\\' or ':' or '\0')
                builder[i] = '_';
        }
        return builder.ToString();
    }

    /// Имя с номером: «отчёт (2).pdf», «Папка (3)». Расширение — после последней точки, если она не первая.
    public static string Numbered(string name, int number, bool isDirectory)
    {
        var dot = name.LastIndexOf('.');
        return !isDirectory && dot > 0 ? $"{name[..dot]} ({number}){name[dot..]}" : $"{name} ({number})";
    }

    /// Пути на диске для элементов описания (относительные, через «/»), по порядку Items.
    /// windows = true — дополнительно заменить то, что нельзя в именах Windows (WindowsName).
    /// Совпадающие без учёта регистра имена в одной папке получают номер: «a (2).txt».
    public static IReadOnlyList<string> LocalPaths(IReadOnlyList<FileItem> items, bool windows)
    {
        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var result = new List<string>(items.Count);
        foreach (var item in items)
        {
            var slash = item.Path.LastIndexOf('/');
            var parent = slash >= 0 ? item.Path[..slash] : "";
            var name = slash >= 0 ? item.Path[(slash + 1)..] : item.Path;
            var localParent = parent.Length == 0 ? "" : mapped.GetValueOrDefault(parent, parent);
            var local = windows ? WindowsName(name) : name;
            if (!used.TryGetValue(localParent, out var taken))
                used[localParent] = taken = [];
            var baseName = local;
            for (var number = 2; taken.Contains(local.ToLowerInvariant()); number++)
                local = Numbered(baseName, number, item.IsDirectory);
            taken.Add(local.ToLowerInvariant());
            var path = localParent.Length == 0 ? local : $"{localParent}/{local}";
            mapped[item.Path] = path;
            result.Add(path);
        }
        return result;
    }

    /// Имя, допустимое в Windows: &lt; &gt; " | ? * и управляющие символы — на «_», точка или пробел в конце — на «_»,
    /// зарезервированные имена устройств (CON, NUL, COM1…) — с «_» в начале.
    public static string WindowsName(string name)
    {
        var builder = new StringBuilder(name);
        for (var i = 0; i < builder.Length; i++)
        {
            if (builder[i] < 0x20 || builder[i] is '<' or '>' or '"' or '|' or '?' or '*')
                builder[i] = '_';
        }
        for (var i = builder.Length - 1; i >= 0 && builder[i] is '.' or ' '; i--)
            builder[i] = '_';
        var result = builder.ToString();
        var dot = result.IndexOf('.');
        var stem = (dot >= 0 ? result[..dot] : result).Trim().ToUpperInvariant();
        var isNumbered = stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
            && stem[3] is >= '1' and <= '9';
        return ReservedWindowsNames.Contains(stem) || isNumbered ? "_" + result : result;
    }
}

/// Что отправлять в описании: элементы и локальные пути для них (docs/protocol.md, «Файлы» и
/// «Поведение сторон → Файлы в буфере»). Совпадает с FileTree.swift.
/// - Символические ссылки (и точки соединения NTFS) и особые файлы пропускаются, .DS_Store внутри папок — тоже.
///   Файлы OneDrive «по запросу» — тоже точки повторной обработки, но не ссылки: они отправляются.
/// - Имена — в NFC, «\» и «:» заменяются на «_», совпадения в одной папке получают номер.
/// - Больше 10 000 элементов, больше 10 ГиБ, слишком длинное имя или путь — FileOfferException.
public sealed record FileTree(IReadOnlyList<FileItem> Items, IReadOnlyList<string> LocalPaths, long Total)
{
    /// Служебный файл Finder: в папках не передаётся.
    private const string FinderMetadata = ".DS_Store";

    private static readonly EnumerationOptions AllEntries = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// Обойти выбранное. Читает диск — вызывать не на потоке интерфейса.
    public static FileTree Build(IReadOnlyList<string> roots)
    {
        var items = new List<FileItem>();
        var paths = new List<string>();
        long total = 0;
        var used = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var root in roots)
                Visit(Path.GetFullPath(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), null, used);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            throw new FileOfferException(FileOfferFailure.Unreadable, e.Message);
        }
        if (items.Count == 0)
            throw new FileOfferException(FileOfferFailure.Empty, "нечего отправлять");
        return new FileTree(items, paths, total);

        void Visit(string path, string? parent, HashSet<string> siblings)
        {
            var isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path))
                throw new FileOfferException(FileOfferFailure.Unreadable, $"нет «{path}»");
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            VisitInfo(info, parent, siblings);
        }

        void VisitInfo(FileSystemInfo info, string? parent, HashSet<string> siblings)
        {
            if (info.LinkTarget is not null)
                return;
            var isDirectory = info is DirectoryInfo;
            if (!isDirectory && SpecialFiles.IsSpecial(info))
                return;

            var baseName = FileNames.WireName(info.Name);
            var name = baseName;
            for (var number = 2; siblings.Contains(name); number++)
                name = FileNames.Numbered(baseName, number, isDirectory);
            siblings.Add(name);
            if (FileNames.NameProblem(name) is not null)
                throw new FileOfferException(FileOfferFailure.NameTooLong, $"имя «{name}»");
            var path = parent is null ? name : $"{parent}/{name}";
            if (Encoding.UTF8.GetByteCount(path) > Protocol.MaxFilePathBytes)
                throw new FileOfferException(FileOfferFailure.NameTooLong, $"путь «{path}»");
            if (items.Count >= Protocol.MaxFileItems)
                throw new FileOfferException(FileOfferFailure.TooManyItems, $"больше {Protocol.MaxFileItems} элементов");

            if (info is FileInfo file)
            {
                total += file.Length;
                if (total > Protocol.MaxFileTotalBytes)
                    throw new FileOfferException(FileOfferFailure.TooLarge, "больше 10 ГиБ");
                items.Add(FileItem.File(path, file.Length));
                paths.Add(file.FullName);
                return;
            }
            var directory = (DirectoryInfo)info;
            items.Add(FileItem.Directory(path));
            paths.Add(directory.FullName);
            var children = directory.EnumerateFileSystemInfos("*", AllEntries)
                .Where(child => child.Name != FinderMetadata)
                .OrderBy(child => child.Name, StringComparer.Ordinal)
                .ToList();
            var childNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in children)
                VisitInfo(child, path, childNames);
        }
    }
}

/// Особые файлы (устройства, каналы, сокеты): в описание не попадают, их чтение может зависнуть.
internal static partial class SpecialFiles
{
    public static bool IsSpecial(FileSystemInfo info)
    {
        if (OperatingSystem.IsWindows())
            return (info.Attributes & FileAttributes.Device) != 0;
        return UnixFileType(info.FullName) is { } type && type != 0x8000 && type != 0x4000;
    }

    /// Тип файла из st_mode (S_IFMT) по lstat; null — узнать не удалось. Только macOS и Linux:
    /// ядро .NET тип файла наружу не отдаёт, а на Windows (где работает приложение) особых файлов нет.
    private static int? UnixFileType(string path)
    {
        var buffer = new byte[512];
        int modeOffset;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                // struct stat (64-битный inode): dev_t (4 байта), затем mode_t (2 байта).
                var result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? LStatInode64(path, buffer) : LStat(path, buffer);
                if (result != 0)
                    return null;
                modeOffset = 4;
            }
            else if (OperatingSystem.IsLinux())
            {
                if (LStat(path, buffer) != 0)
                    return null;
                modeOffset = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => 24,
                    Architecture.Arm64 => 16,
                    _ => -1,
                };
                if (modeOffset < 0)
                    return null;
            }
            else
            {
                return null;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(modeOffset)) & 0xF000;
    }

    [DllImport("libc", EntryPoint = "lstat", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libc", EntryPoint = "lstat$INODE64", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int LStatInode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);
}

/// Почему не удалось отправить описание. Имена совпадают с FileOfferFailure.code на Mac.
public enum FileOfferFailure
{
    /// Нечего отправлять: ничего не выбрано или всё пропущено (ссылки, особые файлы).
    Empty,
    /// Больше 10 000 элементов.
    TooManyItems,
    /// Больше 10 ГиБ.
    TooLarge,
    /// Имя длиннее 255 байт UTF-8 или путь длиннее 1024 байт.
    NameTooLong,
    /// Не удалось прочитать папку или свойства файла.
    Unreadable,
    /// «Передавать файлы» выключено.
    Disabled,
}

public sealed class FileOfferException(FileOfferFailure failure, string message) : Exception(message)
{
    public FileOfferFailure Failure { get; } = failure;
}

/// Почему не удалось скачать. Код для интерфейса; имена совпадают с FileTransferFailure.code на Mac.
public enum FileTransferFailure
{
    /// Нет сеанса с устройством-источником или он оборвался: «устройство недоступно».
    DeviceUnavailable,
    /// Источник не знает описание или элемент (устарело — больше часа, или там выключены файлы).
    NotFound,
    /// Файл на источнике изменился или пропал после копирования.
    Changed,
    /// Источник не смог прочитать файл (или долго не присылает данные).
    Unavailable,
    /// Скачивание отменено.
    Cancelled,
    /// Не удалось записать на диск.
    WriteFailed,
    /// Данные пришли не те, что обещаны (не та длина и т. п.).
    ProtocolError,
}

/// Ошибка скачивания. IOException — чтобы поток OpenFileStream бросал привычное для потоков исключение.
public sealed class FileTransferException(FileTransferFailure failure, string? detail = null)
    : IOException(Describe(failure, detail))
{
    public FileTransferFailure Failure { get; } = failure;

    private static string Describe(FileTransferFailure failure, string? detail)
    {
        var text = failure switch
        {
            FileTransferFailure.DeviceUnavailable => "Устройство недоступно",
            FileTransferFailure.NotFound => "На другом устройстве этих файлов уже нет",
            FileTransferFailure.Changed => "Файл изменился после копирования",
            FileTransferFailure.Unavailable => "Другое устройство не смогло прочитать файл",
            FileTransferFailure.Cancelled => "Скачивание отменено",
            FileTransferFailure.WriteFailed => "Не удалось записать файл",
            _ => "Получены не те данные",
        };
        return string.IsNullOrEmpty(detail) ? text : $"{text}: {detail}";
    }
}

public static class FileTransferFailures
{
    /// По reason из file_error; незнакомая причина — Unavailable.
    public static FileTransferFailure FromPeerReason(string reason) => reason switch
    {
        "not_found" => FileTransferFailure.NotFound,
        "changed" => FileTransferFailure.Changed,
        _ => FileTransferFailure.Unavailable,
    };

    /// Код для любой ошибки скачивания.
    public static FileTransferFailure Of(Exception e) => e switch
    {
        FileTransferException transfer => transfer.Failure,
        OperationCanceledException => FileTransferFailure.Cancelled,
        _ => FileTransferFailure.DeviceUnavailable,
    };
}

/// Сообщение о файлах (или двоичный кадр) не прошло проверку: пишется в журнал и пропускается, сеанс продолжается.
public sealed class FileMessageException(string message) : ProtocolException(message);

/// Кэш полученных файлов: &lt;root&gt;/&lt;id&gt;/ (docs/protocol.md, «Поведение сторон»). Папку root выбирает приложение.
public static class IncomingCache
{
    /// Сколько хранить: старше суток удаляются.
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    /// Папка для описания id. id проверен при разборе (32 символа hex), так что за пределы root не выйти.
    public static string DirectoryFor(string root, string offerId) => Path.Combine(root, offerId);

    /// Удалить из root всё, что изменялось раньше, чем maxAge назад. Возвращает, сколько удалено.
    public static int Clean(string root, TimeSpan? maxAge = null, DateTime? nowUtc = null)
    {
        var limit = (nowUtc ?? DateTime.UtcNow) - (maxAge ?? MaxAge);
        var removed = 0;
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(root).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        foreach (var entry in entries)
        {
            try
            {
                var time = entry.LastWriteTimeUtc > entry.CreationTimeUtc ? entry.LastWriteTimeUtc : entry.CreationTimeUtc;
                if (time >= limit)
                    continue;
                if (entry is DirectoryInfo directory)
                    directory.Delete(recursive: true);
                else
                    entry.Delete();
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Кэш файлов: не удалось удалить {entry.FullName}: {e.Message}");
            }
        }
        return removed;
    }
}
