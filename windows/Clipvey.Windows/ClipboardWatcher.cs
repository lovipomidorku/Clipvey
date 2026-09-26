using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Clipvey.Core;

namespace Clipvey.Windows;

/// Следит за буфером обмена Windows (AddClipboardFormatListener) и записывает в него текст и картинки с других устройств.
/// Работает на потоке интерфейса: буфер открывается им же (OpenClipboard с окном-наблюдателем).
/// Перевод картинок (DIB → PNG, PNG/JPEG → DIB) идёт в фоне, чтобы большие картинки не подвешивали интерфейс.
///
/// Что отправляется (docs/protocol.md, «Поведение сторон»):
/// - ничего, если в буфере маркер ClipveyRemote (записано нами) или формат «секретного» содержимого;
/// - картинка — если картинки можно отправлять и текста нет или это одна ссылка;
///   формат PNG как есть, иначе CF_DIBV5 / CF_DIB, переведённый в PNG;
/// - иначе текст.
internal sealed class ClipboardWatcher : NativeWindow, IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private static readonly IntPtr MessageOnlyParent = new(-3);

    private const uint CfUnicodeText = 13;
    private const uint CfDib = 8;
    private const uint CfDibV5 = 17;

    /// Отметка «пришло с другого устройства»: такое содержимое не отправляется обратно.
    private const string RemoteFormatName = "ClipveyRemote";

    /// Так менеджеры паролей и системные программы просят не передавать содержимое буфера.
    private static readonly string[] SecretFormatNames = ["ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore"];

    /// Исходный DIB больше этого не читаем: 8K-скриншот в 32 битах — около 130 МБ.
    private const int MaxSourceDibBytes = 400 * 1024 * 1024;

    private readonly uint _remoteFormat = Register(RemoteFormatName);
    private readonly uint[] _secretFormats = [.. SecretFormatNames.Select(Register)];
    private readonly uint _pngFormat = Register("PNG");
    private readonly uint _jfifFormat = Register("JFIF");

    private readonly Action<string> _onCopy;
    private readonly Action<byte[], string> _onCopyImage;
    private readonly Func<bool> _shouldRead;
    private readonly Func<bool> _shouldReadImages;
    private readonly SynchronizationContext _ui;

    /// Номер последней записи с другого устройства: запись картинки после перевода отменяется, если есть новее.
    private int _writeGeneration;

    /// onCopy — скопирован текст (переводы строк \n); onCopyImage — картинка (PNG или JPEG, до 20 МиБ).
    /// shouldRead — есть ли кому отправлять; shouldReadImages — есть ли кому отправлять картинки.
    public ClipboardWatcher(Action<string> onCopy, Action<byte[], string> onCopyImage, Func<bool> shouldRead, Func<bool> shouldReadImages)
    {
        _onCopy = onCopy;
        _onCopyImage = onCopyImage;
        _shouldRead = shouldRead;
        _shouldReadImages = shouldReadImages;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        CreateHandle(new CreateParams { Parent = MessageOnlyParent });
        if (!AddClipboardFormatListener(Handle))
            Log.Write($"Не удалось подписаться на изменения буфера (ошибка {Marshal.GetLastWin32Error()})");
    }

    // MARK: - Запись

    /// Записать текст с другого устройства (переводы строк \n превращаются в \r\n).
    public void WriteRemote(string text)
    {
        _writeGeneration++;
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        data.SetData(RemoteFormatName, "1");
        try
        {
            Clipboard.SetDataObject(data, copy: true, retryTimes: 10, retryDelay: 50);
            Log.Write($"Записано в буфер: {text.Length} символов");
        }
        catch (ExternalException e)
        {
            Log.Write($"Не удалось записать в буфер (занят другой программой): {e.Message}");
        }
    }

    /// Записать картинку с другого устройства:
    /// - PNG: формат PNG (с прозрачностью) и CF_DIB для старых программ (прозрачное — на белом);
    /// - JPEG: CF_DIB и JFIF.
    /// Перевод в DIB — в фоне; если за это время буфер изменился, запись отменяется.
    public void WriteRemoteImage(byte[] data, string mime)
    {
        var generation = ++_writeGeneration;
        var sequence = GetClipboardSequenceNumber();
        var jpeg = mime == "image/jpeg";
        Task.Run(() => DecodeToDib(data, jpeg)).ContinueWith(task => _ui.Post(_ =>
        {
            if (generation != _writeGeneration)
            {
                Log.Write("Картинка не записана в буфер: уже пришло что-то новее");
                return;
            }
            if (GetClipboardSequenceNumber() != sequence)
            {
                Log.Write("Картинка не записана в буфер: пока она готовилась, буфер изменился");
                return;
            }
            var dib = task.IsCompletedSuccessfully ? task.Result : null;
            var formats = new List<(uint Format, byte[] Data)>();
            if (!jpeg)
                formats.Add((_pngFormat, data));
            if (dib is not null)
                formats.Add((CfDib, dib));
            if (jpeg)
                formats.Add((_jfifFormat, data));
            formats.Add((_remoteFormat, "1"u8.ToArray()));
            _ = WriteFormatsAsync(formats, generation, $"картинка {mime}, {data.Length} байт{(dib is null ? ", без DIB" : "")}");
        }, null), TaskScheduler.Default);
    }

    /// Перевести PNG/JPEG в CF_DIB через GDI+. null — не удалось (причина в журнале).
    private static byte[]? DecodeToDib(byte[] data, bool jpeg)
    {
        try
        {
            // Поток должен жить, пока жив Bitmap.
            using var stream = new MemoryStream(data, writable: false);
            using var bitmap = new Bitmap(stream);
            if (jpeg)
                ApplyExifOrientation(bitmap);
            var width = bitmap.Width;
            var height = bitmap.Height;
            if ((long)width * height * 4 > int.MaxValue || (long)width * height > Dib.MaxPixels)
            {
                Log.Write($"Картинка {width}×{height} слишком большая для DIB");
                return null;
            }
            var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var stride = width * 4;
                var pixels = new byte[(long)stride * height];
                for (var y = 0; y < height; y++)
                    Marshal.Copy(bits.Scan0 + y * bits.Stride, pixels, y * stride, stride);
                return Dib.FromBgra(width, height, pixels, stride);
            }
            finally
            {
                bitmap.UnlockBits(bits);
            }
        }
        catch (Exception e) when (e is ArgumentException or OutOfMemoryException or ExternalException or InvalidOperationException)
        {
            Log.Write($"Не удалось перевести картинку в DIB: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// GDI+ не учитывает поворот из EXIF (тег 0x0112) — фото с iPhone повернули бы боком.
    private static void ApplyExifOrientation(Bitmap bitmap)
    {
        const int OrientationTag = 0x0112;
        if (!bitmap.PropertyIdList.Contains(OrientationTag))
            return;
        var value = bitmap.GetPropertyItem(OrientationTag)?.Value;
        if (value is not { Length: >= 2 })
            return;
        var flip = BitConverter.ToUInt16(value, 0) switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (flip != RotateFlipType.RotateNoneFlipNone)
            bitmap.RotateFlip(flip);
    }

    /// Положить форматы в буфер одной записью. Буфер может быть занят другой программой — повторяем, не блокируя интерфейс.
    private async Task WriteFormatsAsync(List<(uint Format, byte[] Data)> formats, int generation, string description)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (generation != _writeGeneration)
                return;
            if (OpenClipboard(Handle))
            {
                try
                {
                    if (!EmptyClipboard())
                    {
                        Log.Write($"Не удалось очистить буфер (ошибка {Marshal.GetLastWin32Error()})");
                        return;
                    }
                    foreach (var (format, data) in formats)
                    {
                        if (!SetBytes(format, data))
                            Log.Write($"Не удалось положить в буфер формат {FormatName(format)} (ошибка {Marshal.GetLastWin32Error()})");
                    }
                }
                finally
                {
                    CloseClipboard();
                }
                Log.Write($"Записана в буфер {description}: {string.Join(", ", formats.Select(f => FormatName(f.Format)))}");
                return;
            }
            await Task.Delay(50);
        }
        Log.Write($"Не удалось записать в буфер (занят другой программой): {description}");
    }

    private static bool SetBytes(uint format, byte[] data)
    {
        var memory = GlobalAlloc(GmemMoveable, (UIntPtr)Math.Max(1, data.Length));
        if (memory == IntPtr.Zero)
            return false;
        var pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(memory);
            return false;
        }
        try
        {
            Marshal.Copy(data, 0, pointer, data.Length);
        }
        finally
        {
            GlobalUnlock(memory);
        }
        if (SetClipboardData(format, memory) != IntPtr.Zero)
            return true; // Теперь памятью владеет система.
        GlobalFree(memory);
        return false;
    }

    // MARK: - Чтение

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmClipboardUpdate)
        {
            try
            {
                OnClipboardUpdate();
            }
            catch (Exception e)
            {
                Log.Write($"Ошибка чтения буфера: {e}");
            }
        }
        base.WndProc(ref m);
    }

    /// Что взять из буфера: текст или исходные данные картинки (PNG либо DIB).
    private sealed record Snapshot(string? Text, byte[]? Image, uint ImageFormat, string Formats);

    private void OnClipboardUpdate()
    {
        if (!_shouldRead())
            return;
        // До открытия буфера: пока он открыт, другие программы ждут.
        var wantImages = _shouldReadImages();
        // Программа-источник может ещё держать буфер открытым — пробуем несколько раз.
        Snapshot? snapshot = null;
        var opened = false;
        for (var attempt = 0; attempt < 5 && !opened; attempt++)
        {
            opened = OpenClipboard(Handle);
            if (!opened)
                Thread.Sleep(40);
        }
        if (!opened)
        {
            Log.Write("Буфер обмена занят другой программой — изменение пропущено");
            return;
        }
        try
        {
            snapshot = ReadOpened(wantImages);
        }
        finally
        {
            CloseClipboard();
        }
        if (snapshot is null)
            return;

        if (snapshot.Image is { } image)
        {
            SendImage(image, snapshot.ImageFormat, snapshot.Formats);
            return;
        }
        if (snapshot.Text is { Length: > 0 } text)
            _onCopy(text.Replace("\r\n", "\n"));
    }

    /// Буфер открыт. Сначала только список форматов и текст: картинку запрашиваем, лишь если отправлять будем её —
    /// Word и Excel рисуют картинку по запросу, и это долго.
    private Snapshot? ReadOpened(bool wantImages)
    {
        var formats = new List<uint>();
        for (uint format = 0; (format = EnumClipboardFormats(format)) != 0;)
            formats.Add(format);
        if (formats.Contains(_remoteFormat))
            return null;
        if (formats.Any(_secretFormats.Contains))
        {
            Log.Write("Секретное содержимое (пароль) не передаётся");
            return null;
        }

        var text = formats.Contains(CfUnicodeText) ? ReadText() : null;
        // PNG, иначе тот из CF_DIBV5 / CF_DIB, что идёт первым: форматы перечисляются в порядке,
        // в каком их положила программа, а второй DIB Windows выводит из первого сам.
        uint? imageFormat = formats.Contains(_pngFormat) ? _pngFormat
            : formats.FirstOrDefault(format => format is CfDib or CfDibV5) is var dib and not 0 ? dib
            : null;
        if (imageFormat is { } chosen)
        {
            var auxiliary = ClipboardRules.TextIsAuxiliary(text);
            if (!wantImages || !auxiliary)
            {
                // Картинка есть, но уходит текст (или ничего): причина и форматы — для разбора по журналу.
                var reason = !wantImages ? "картинки выключены или их некому отправить" : "рядом с ней текст";
                Log.Write($"Картинка из буфера не отправлена: {reason} (форматы: {string.Join(", ", formats.Select(FormatName))})");
            }
            else
            {
                var names = string.Join(", ", formats.Select(FormatName));
                var data = ReadBytes(chosen, chosen == _pngFormat ? Protocol.MaxImageBytes : MaxSourceDibBytes, out var tooLarge);
                if (tooLarge)
                {
                    Log.Write($"Картинка в буфере ({FormatName(chosen)}) слишком большая — не передаётся");
                    return null;
                }
                if (data is { Length: > 0 })
                    return new Snapshot(text, data, chosen, names);
                Log.Write($"Не удалось прочитать картинку {FormatName(chosen)} из буфера (форматы: {names})");
            }
        }
        return new Snapshot(text, null, 0, "");
    }

    /// PNG — как есть, DIB — в PNG. Перевод в фоне; результат — снова на потоке интерфейса.
    private void SendImage(byte[] data, uint format, string formats)
    {
        Log.Write($"Картинка из буфера: {FormatName(format)}, {data.Length} байт (форматы: {formats})");
        if (format == _pngFormat)
        {
            DeliverImage(TrimPng(data), "image/png");
            return;
        }
        Task.Run(() => Dib.ToImage(data)).ContinueWith(task => _ui.Post(_ =>
        {
            if (!task.IsCompletedSuccessfully)
            {
                Log.Write($"Не удалось перевести DIB в PNG: {task.Exception?.GetBaseException().Message}");
                return;
            }
            var converted = task.Result;
            if (converted.Data is null || converted.Mime is null)
            {
                Log.Write($"Не удалось перевести DIB в PNG: {converted.Error}");
                return;
            }
            DeliverImage(converted.Data, converted.Mime);
        }, null), TaskScheduler.Default);
    }

    /// GlobalSize может быть больше записанного: отрезаем всё после чанка IEND. Не разобрался — как есть.
    private static byte[] TrimPng(byte[] data)
    {
        var at = 8L;
        while (at + 12 <= data.Length)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)at));
            var end = at + 12 + length;
            if (end > data.Length)
                break;
            if (data.AsSpan((int)at + 4, 4).SequenceEqual("IEND"u8))
                return end == data.Length ? data : data[..(int)end];
            at = end;
        }
        return data;
    }

    private void DeliverImage(byte[] data, string mime)
    {
        if (data.Length > Protocol.MaxImageBytes)
        {
            Log.Write($"Картинка {data.Length} байт больше 20 МиБ — не передаётся");
            return;
        }
        _onCopyImage(data, mime);
    }

    private static string? ReadText()
    {
        var handle = GetClipboardData(CfUnicodeText);
        if (handle == IntPtr.Zero)
            return null;
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
            return null;
        try
        {
            var chars = (int)Math.Min((ulong)GlobalSize(handle) / 2, int.MaxValue);
            var text = Marshal.PtrToStringUni(pointer, chars);
            var end = text.IndexOf('\0');
            return end >= 0 ? text[..end] : text;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static byte[]? ReadBytes(uint format, int limit, out bool tooLarge)
    {
        tooLarge = false;
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero)
            return null;
        var size = (ulong)GlobalSize(handle);
        if (size > (ulong)limit)
        {
            tooLarge = true;
            return null;
        }
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
            return null;
        try
        {
            var data = new byte[size];
            Marshal.Copy(pointer, data, 0, data.Length);
            return data;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static uint Register(string name)
    {
        var format = RegisterClipboardFormat(name);
        if (format == 0)
            Log.Write($"Не удалось зарегистрировать формат буфера {name} (ошибка {Marshal.GetLastWin32Error()})");
        return format;
    }

    /// Имя формата для журнала (без содержимого).
    private static string FormatName(uint format)
    {
        switch (format)
        {
            case 1: return "CF_TEXT";
            case 2: return "CF_BITMAP";
            case 7: return "CF_OEMTEXT";
            case CfDib: return "CF_DIB";
            case CfUnicodeText: return "CF_UNICODETEXT";
            case 15: return "CF_HDROP";
            case 16: return "CF_LOCALE";
            case CfDibV5: return "CF_DIBV5";
        }
        var name = new StringBuilder(256);
        return GetClipboardFormatName(format, name, name.Capacity) > 0 ? name.ToString() : format.ToString();
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }

    private const uint GmemMoveable = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW")]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClipboardFormatNameW")]
    private static extern int GetClipboardFormatName(uint format, StringBuilder name, int maxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
