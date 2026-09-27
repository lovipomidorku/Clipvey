using System.Security.Cryptography;
using System.Text;

namespace Clipvey.Core;

/// Одно копирование бывает двумя изменениями буфера подряд с тем же содержимым: программы на WinForms
/// (Clipboard.SetDataObject с copy: true) кладут данные и сразу «закрепляют» их (OleFlushClipboard).
/// Второе не отправляется: то же содержимое (текст, картинка или файлы) в течение Window после отправки — повтор.
/// - Отсчёт — от отправки, а не от последнего повтора.
/// - Запись с другого устройства (Reset) и другое содержимое сбрасывают его: если после них скопировать то же ещё
///   раз, оно снова отправится (у других устройств в буфере уже другое).
/// Не потокобезопасен: все вызовы — из одного потока (интерфейса).
public sealed class RepeatedCopyFilter(Func<long>? clock = null)
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    /// Время в миллисекундах (по умолчанию Environment.TickCount64).
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private byte[]? _last;
    private long _sentAt;

    /// true — повтор только что отправленного: не отправлять. false — отправлять; содержимое запоминается.
    public bool IsRepeat(byte[] key)
    {
        var now = _clock();
        if (_last is not null && now - _sentAt < (long)Window.TotalMilliseconds && _last.AsSpan().SequenceEqual(key))
            return true;
        _last = key;
        _sentAt = now;
        return false;
    }

    /// В буфер записано содержимое с другого устройства.
    public void Reset() => _last = null;

    /// Ключ текста (переводы строк уже приведены к \n).
    public static byte[] TextKey(string text) => Key((byte)'t', Encoding.UTF8.GetBytes(text));

    /// Ключ картинки: данные из буфера как есть (PNG или DIB).
    public static byte[] ImageKey(ReadOnlySpan<byte> data) => Key((byte)'i', data);

    /// Ключ файлов: пути по порядку, без учёта регистра (как в файловой системе Windows).
    public static byte[] FilesKey(IEnumerable<string> paths) =>
        Key((byte)'f', Encoding.UTF8.GetBytes(string.Join('\0', paths.Select(path => path.ToUpperInvariant()))));

    private static byte[] Key(byte kind, ReadOnlySpan<byte> data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([kind]);
        hash.AppendData(data);
        return hash.GetHashAndReset();
    }
}
