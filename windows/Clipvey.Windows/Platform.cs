using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using Clipvey.Core;

namespace Clipvey.Windows;

/// Где программа хранит данные. В режиме проверки (--test --data DIR) всё — в DIR, и копия не пересекается
/// с рабочей: свои ключ, устройства, настройки, журнал, полученные файлы, мьютекс и событие показа панели.
/// Configure вызывается первым делом в Main, до любого обращения к настройкам.
internal static class AppPaths
{
    /// %APPDATA%\Clipvey: ключ устройства, список связанных устройств, настройки.
    public static string DataDirectory { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clipvey");

    /// %LOCALAPPDATA%\Clipvey\clipvey.log: журнал для диагностики.
    public static string LogFile { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipvey", "clipvey.log");

    /// %LOCALAPPDATA%\Clipvey\Incoming: небольшие полученные файлы (IncomingCache), старше суток удаляются.
    public static string IncomingDirectory { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipvey", "Incoming");

    /// Куда «Загрузить» кладёт большие файлы: «Загрузки»\Clipvey (известная папка FOLDERID_Downloads — её можно
    /// перенести на другой диск). В режиме проверки — --downloads DIR или DIR данных\Downloads: настоящие
    /// «Загрузки» проверки не трогают.
    public static string DownloadsDirectory => _downloads ??= Path.Combine(KnownDownloads(), "Clipvey");
    private static string? _downloads;

    /// Режим проверки со своей папкой данных (--test --data DIR).
    public static bool IsolatedTest { get; private set; }

    /// Имя мьютекса «уже запущен» и приставка имён событий: у копии для проверок — свои.
    public static string InstanceName { get; private set; } = @"Local\Clipvey";

    /// Режим проверки: --probe ПУТЬ — после запуска обойти ПУТЬ так же, как при отправке файлов (только имена,
    /// размеры и атрибуты, без чтения содержимого), и записать итог в журнал. Ничего не отправляется.
    public static string? ProbePath { get; private set; }

    /// Режим проверки: --toast-demo ВИД[,ВИД…] — показать виды окошка по очереди без сети (offer, progress, done,
    /// error, notice, receiving, received, chain — смена вида в показанном окошке), --toast-demo-step МС — сколько
    /// держать каждый (по умолчанию 3500). Перед каждым видом окошко прячется: проверяется первое появление.
    public static IReadOnlyList<string> ToastDemo { get; private set; } = [];
    public static int ToastDemoStep { get; private set; } = 3500;

    /// Режим проверки: --toast-no-redraw — показывать окошко без принудительной перерисовки (как до исправления
    /// «пустого окошка»), чтобы снимком сравнить.
    public static bool ToastNoRedraw { get; private set; }

    public static void Configure(string[] args)
    {
        var index = Array.IndexOf(args, "--data");
        if (!args.Contains("--test") || index < 0 || index + 1 >= args.Length)
            return;
        var directory = Path.GetFullPath(args[index + 1]);
        IsolatedTest = true;
        DataDirectory = directory;
        LogFile = Path.Combine(directory, "clipvey.log");
        IncomingDirectory = Path.Combine(directory, "Incoming");
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(directory.ToLowerInvariant())))[..12];
        InstanceName = $@"Local\Clipvey.Test.{hash}";
        var probe = Array.IndexOf(args, "--probe");
        if (probe >= 0 && probe + 1 < args.Length)
            ProbePath = args[probe + 1];
        var demo = Array.IndexOf(args, "--toast-demo");
        if (demo >= 0 && demo + 1 < args.Length)
            ToastDemo = args[demo + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var step = Array.IndexOf(args, "--toast-demo-step");
        if (step >= 0 && step + 1 < args.Length && int.TryParse(args[step + 1], out var milliseconds))
            ToastDemoStep = Math.Clamp(milliseconds, 500, 60_000);
        ToastNoRedraw = args.Contains("--toast-no-redraw");
        var downloads = Array.IndexOf(args, "--downloads");
        _downloads = downloads >= 0 && downloads + 1 < args.Length
            ? Path.GetFullPath(args[downloads + 1])
            : Path.Combine(directory, "Downloads");
    }

    /// Папка «Загрузки» пользователя. Не получилось узнать — %USERPROFILE%\Downloads.
    private static string KnownDownloads()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
        if (SHGetKnownFolderPath(downloads, 0, IntPtr.Zero, out var pointer) == 0)
        {
            try
            {
                if (Marshal.PtrToStringUni(pointer) is { Length: > 0 } path)
                    return path;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        Log.Write("Не удалось узнать папку «Загрузки» (SHGetKnownFolderPath), беру %USERPROFILE%\\Downloads");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}

/// Ключ устройства, зашифрованный DPAPI: прочитать его может только эта учётная запись на этом компьютере.
internal sealed class DpapiSecretStore(string directory) : ISecretStore
{
    public byte[]? Load(string name)
    {
        var path = Path.Combine(directory, name + ".dpapi");
        return File.Exists(path)
            ? ProtectedData.Unprotect(File.ReadAllBytes(path), optionalEntropy: null, DataProtectionScope.CurrentUser)
            : null;
    }

    public void Save(string name, byte[] data)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(
            Path.Combine(directory, name + ".dpapi"),
            ProtectedData.Protect(data, optionalEntropy: null, DataProtectionScope.CurrentUser));
    }
}

/// Журнал в файл. При размере больше 2 МБ старый журнал переименовывается в clipvey.old.log.
internal static class FileLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static void Start()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.LogFile)!);
        Log.Sink = line =>
        {
            lock (Gate)
            {
                try
                {
                    var info = new FileInfo(AppPaths.LogFile);
                    if (info.Exists && info.Length > MaxBytes)
                        File.Move(AppPaths.LogFile, Path.ChangeExtension(AppPaths.LogFile, ".old.log"), overwrite: true);
                    File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
                }
                catch (IOException)
                {
                    // Журнал не должен ломать работу программы.
                }
            }
        };
        Log.Write($"Clipvey {Version} запущен, Windows {Environment.OSVersion.Version}");
    }

    /// Версия программы (InformationalVersion, из файла VERSION).
    private static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    public static void Open()
    {
        if (File.Exists(AppPaths.LogFile))
            Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true });
    }
}

/// Автозапуск при входе в Windows: запись в HKCU\...\Run.
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Clipvey";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        // Копия для проверок не должна прописывать себя (или убирать рабочую копию) в автозапуск.
        if (AppPaths.IsolatedTest)
        {
            Log.Write($"Автозапуск в режиме проверки не меняется (просили {(enabled ? "включить" : "выключить")})");
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Write($"Автозапуск {(enabled ? "включён" : "выключен")}");
    }

    /// IsEnabled без исключений: реестр может быть недоступен (политики) — тогда «выключен».
    public static bool SafeIsEnabled()
    {
        try
        {
            return IsEnabled;
        }
        catch (Exception e)
        {
            Log.Write($"Не удалось прочитать автозапуск: {e.Message}");
            return false;
        }
    }

    /// Set без исключений. Возвращает, включён ли автозапуск после попытки.
    public static bool SafeSet(bool enabled)
    {
        try
        {
            Set(enabled);
        }
        catch (Exception e)
        {
            Log.Write($"Не удалось изменить автозапуск: {e.Message}");
        }
        return SafeIsEnabled();
    }
}

internal static class AppIcon
{
    public static Icon Load(Size size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Clipvey.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream, size);
    }

    /// Картинка значка не меньше side пикселей (если есть). Внутри Clipvey.ico — PNG 16…256 px:
    /// берём PNG напрямую, чтобы не зависеть от того, как Icon.ToBitmap обходится с PNG-кадрами.
    /// PNG-кадр значка не меньше side пикселей (null — в Clipvey.ico нет PNG-кадров).
    public static byte[]? Png(int side)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Clipvey.ico");
        if (stream is null)
            return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return BestPng(memory.ToArray(), side);
    }

    public static Bitmap LoadBitmap(int side)
    {
        try
        {
            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Clipvey.ico");
            if (stream is not null)
            {
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                if (BestPng(memory.ToArray(), side) is { } png)
                {
                    // Копия, чтобы картинка не зависела от потока (GDI+ читает поток лениво).
                    using var pngStream = new MemoryStream(png);
                    using var decoded = new Bitmap(pngStream);
                    return new Bitmap(decoded);
                }
            }
        }
        catch (Exception e)
        {
            Log.Write($"Не удалось прочитать Clipvey.ico: {e.Message}");
        }
        using var icon = Load(new Size(side, side));
        return icon.ToBitmap();
    }

    private static byte[]? BestPng(byte[] ico, int side)
    {
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 2) != 1)
            return null;
        var count = BitConverter.ToUInt16(ico, 4);
        (int Side, int Offset, int Length)? best = null;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            if (entry + 16 > ico.Length)
                break;
            var entrySide = ico[entry] == 0 ? 256 : ico[entry];
            var length = BitConverter.ToInt32(ico, entry + 8);
            var offset = BitConverter.ToInt32(ico, entry + 12);
            if (offset < 0 || length < 8 || offset + length > ico.Length || ico[offset] != 0x89 || ico[offset + 1] != (byte)'P')
                continue;
            // Наименьший кадр не меньше нужного; если такого нет — наибольший.
            var better = best is not { } current
                || (entrySide >= side ? current.Side < side || entrySide < current.Side : entrySide > current.Side && current.Side < side);
            if (better)
                best = (entrySide, offset, length);
        }
        return best is { } found ? ico.AsSpan(found.Offset, found.Length).ToArray() : null;
    }
}
