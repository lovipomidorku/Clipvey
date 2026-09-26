using System.Buffers.Binary;
using System.IO.Compression;

namespace Clipvey.Core;

/// Правила буфера обмена, общие с Mac (docs/protocol.md, «Поведение сторон»).
public static class ClipboardRules
{
    /// Текст рядом с картинкой ничего не значит: его нет или это одна ссылка без пробелов
    /// (так копирует картинку браузер). Тогда отправляется картинка, иначе — текст.
    public static bool TextIsAuxiliary(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return true;
        return !trimmed.Any(char.IsWhiteSpace) && (trimmed.Contains("://", StringComparison.Ordinal) || trimmed.StartsWith("data:", StringComparison.Ordinal));
    }
}

/// Картинка в формате DIB (CF_DIB / CF_DIBV5 буфера Windows): упакованный BITMAPINFO + пиксели.
/// Без System.Drawing, поэтому проверяется на Mac (Clipvey.Tests).
public static class Dib
{
    /// Больше пикселей не переводим: 100 млн — это уже 400 МБ в памяти, и PNG всё равно выйдет за 20 МиБ.
    public const long MaxPixels = 100_000_000;

    private const uint BiRgb = 0, BiBitfields = 3, BiJpeg = 4, BiPng = 5, BiAlphaBitfields = 6;

    /// Результат ToImage: данные картинки и mime или причина отказа (для журнала).
    public readonly record struct Converted(byte[]? Data, string? Mime, string? Error);

    /// Перевести упакованный DIB в PNG. BI_PNG и BI_JPEG отдаются как есть.
    /// Альфа берётся, только если заголовок её объявляет (маска альфы в BI_BITFIELDS / BI_ALPHABITFIELDS
    /// или в заголовке V4/V5); если все значения альфы нулевые — картинка считается непрозрачной.
    public static Converted ToImage(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 12)
            return Fail("DIB короче заголовка");
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        int width, height, bitCount;
        uint compression = BiRgb, colorsUsed = 0;
        var paletteEntrySize = 4;
        if (headerSize == 12)
        {
            // BITMAPCOREHEADER: размеры 16-битные, палитра — RGBTRIPLE.
            width = BinaryPrimitives.ReadUInt16LittleEndian(dib[4..]);
            height = BinaryPrimitives.ReadInt16LittleEndian(dib[6..]);
            bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[10..]);
            paletteEntrySize = 3;
        }
        else if (headerSize >= 40 && headerSize <= dib.Length && headerSize <= 1024)
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
            height = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
            bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
            compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
            colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        }
        else
        {
            return Fail($"незнакомый заголовок DIB ({headerSize} байт)");
        }

        var offset = (long)headerSize;
        if (compression is BiPng or BiJpeg)
        {
            // Внутри — готовый PNG или JPEG (sizeImage — его длина).
            var sizeImage = BinaryPrimitives.ReadUInt32LittleEndian(dib[20..]);
            var rest = dib[(int)headerSize..];
            var length = sizeImage > 0 && sizeImage <= rest.Length ? (int)sizeImage : rest.Length;
            return length == 0 ? Fail("пустой BI_PNG/BI_JPEG")
                : new Converted(rest[..length].ToArray(), compression == BiPng ? "image/png" : "image/jpeg", null);
        }
        if (compression is not (BiRgb or BiBitfields or BiAlphaBitfields))
            return Fail($"сжатие DIB {compression} не поддерживается");
        if (bitCount is not (1 or 4 or 8 or 16 or 24 or 32))
            return Fail($"{bitCount} бит на пиксель не поддерживается");
        if (compression != BiRgb && bitCount is not (16 or 32))
            return Fail($"BI_BITFIELDS при {bitCount} битах");
        if (width <= 0 || height == 0 || height == int.MinValue)
            return Fail($"размер DIB {width}×{height}");
        var topDown = height < 0;
        height = Math.Abs(height);
        if ((long)width * height > MaxPixels)
            return Fail($"слишком большая картинка {width}×{height}");

        // Маски цветов: в заголовке V2+ (52+ байт) или сразу после 40-байтного заголовка.
        uint red, green, blue, alpha = 0;
        if (compression == BiRgb)
        {
            (red, green, blue) = bitCount == 16 ? (0x7C00u, 0x03E0u, 0x001Fu) : (0xFF0000u, 0xFF00u, 0xFFu);
            // Заголовок V4/V5 с BI_RGB может явно объявить альфу (так пишут некоторые программы).
            if (bitCount == 32 && headerSize >= 56)
                alpha = BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]);
            if (alpha != 0xFF000000)
                alpha = 0;
        }
        else
        {
            var masksAt = (int)headerSize;
            var maskCount = compression == BiAlphaBitfields ? 4 : 3;
            if (headerSize >= 52)
            {
                masksAt = 40;
                maskCount = headerSize >= 56 ? 4 : 3;
            }
            else
            {
                offset += maskCount * 4;
            }
            if (masksAt + maskCount * 4 > dib.Length)
                return Fail("DIB обрывается на масках цветов");
            red = BinaryPrimitives.ReadUInt32LittleEndian(dib[masksAt..]);
            green = BinaryPrimitives.ReadUInt32LittleEndian(dib[(masksAt + 4)..]);
            blue = BinaryPrimitives.ReadUInt32LittleEndian(dib[(masksAt + 8)..]);
            if (maskCount == 4)
                alpha = BinaryPrimitives.ReadUInt32LittleEndian(dib[(masksAt + 12)..]);
            if (bitCount == 16)
            {
                red &= 0xFFFF;
                green &= 0xFFFF;
                blue &= 0xFFFF;
                alpha &= 0xFFFF;
            }
        }

        // Палитра: biClrUsed записей (бывает и при 16–32 битах), иначе 2^bitCount при ≤ 8 битах.
        byte[]? palette = null;
        var paletteCount = colorsUsed > 0 ? colorsUsed : bitCount <= 8 ? 1u << bitCount : 0;
        if (paletteCount > 0)
        {
            if (paletteCount > 65536)
                return Fail($"палитра из {paletteCount} цветов");
            var paletteBytes = (long)paletteCount * paletteEntrySize;
            if (offset + paletteBytes > dib.Length)
                return Fail("DIB обрывается на палитре");
            if (bitCount <= 8)
            {
                palette = new byte[256 * 3];
                for (var i = 0; i < Math.Min(paletteCount, 256u); i++)
                {
                    var entry = dib.Slice((int)(offset + i * paletteEntrySize), 3);
                    palette[i * 3] = entry[2];
                    palette[i * 3 + 1] = entry[1];
                    palette[i * 3 + 2] = entry[0];
                }
            }
            offset += paletteBytes;
        }
        else if (bitCount <= 8)
        {
            return Fail("нет палитры");
        }

        var stride = ((long)width * bitCount + 31) / 32 * 4;
        if (offset + stride * height > dib.Length)
            return Fail($"DIB {width}×{height}×{bitCount} короче данных ({dib.Length} байт)");

        var hasAlpha = alpha != 0;
        var channels = hasAlpha ? 4 : 3;
        var pixels = new byte[(long)width * height * channels];
        var red8 = new Channel(red);
        var green8 = new Channel(green);
        var blue8 = new Channel(blue);
        var alpha8 = new Channel(alpha);
        var anyAlpha = false;
        for (var y = 0; y < height; y++)
        {
            var sourceRow = topDown ? y : height - 1 - y;
            var row = dib.Slice((int)(offset + sourceRow * stride), (int)stride);
            var target = (long)y * width * channels;
            for (var x = 0; x < width; x++)
            {
                var at = target + (long)x * channels;
                if (bitCount <= 8)
                {
                    var index = bitCount switch
                    {
                        8 => row[x],
                        4 => (row[x >> 1] >> ((x & 1) == 0 ? 4 : 0)) & 0x0F,
                        _ => (row[x >> 3] >> (7 - (x & 7))) & 1,
                    };
                    pixels[at] = palette![index * 3];
                    pixels[at + 1] = palette[index * 3 + 1];
                    pixels[at + 2] = palette[index * 3 + 2];
                    continue;
                }
                uint value = bitCount switch
                {
                    32 => BinaryPrimitives.ReadUInt32LittleEndian(row[(x * 4)..]),
                    24 => (uint)(row[x * 3] | row[x * 3 + 1] << 8 | row[x * 3 + 2] << 16),
                    _ => BinaryPrimitives.ReadUInt16LittleEndian(row[(x * 2)..]),
                };
                if (bitCount == 24)
                {
                    pixels[at] = (byte)(value >> 16);
                    pixels[at + 1] = (byte)(value >> 8);
                    pixels[at + 2] = (byte)value;
                    continue;
                }
                pixels[at] = red8.Read(value);
                pixels[at + 1] = green8.Read(value);
                pixels[at + 2] = blue8.Read(value);
                if (hasAlpha)
                {
                    var a = alpha8.Read(value);
                    pixels[at + 3] = a;
                    anyAlpha |= a != 0;
                }
            }
        }

        if (hasAlpha && !anyAlpha)
        {
            // Альфа объявлена, но везде 0: программа её просто не заполнила. Делаем непрозрачной.
            for (long i = 3; i < pixels.LongLength; i += 4)
                pixels[i] = 255;
        }
        return new Converted(Png.Encode(width, height, pixels, hasAlpha), "image/png", null);
    }

    /// CF_DIB для старых программ: 24 бита, снизу вверх, прозрачность смешана с белым фоном.
    /// bgra — пиксели сверху вниз (порядок байтов B, G, R, A, без предумножения), stride — длина строки в байтах.
    public static byte[] FromBgra(int width, int height, ReadOnlySpan<byte> bgra, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < (long)stride * (height - 1) + width * 4)
            throw new ArgumentException($"неверные размеры картинки {width}×{height}");
        var rowBytes = (width * 3 + 3) / 4 * 4;
        var imageSize = (long)rowBytes * height;
        var result = new byte[40 + imageSize];
        var header = result.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(header, 40);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 24);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], BiRgb);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], 3780); // 96 DPI
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], 3780);
        for (var y = 0; y < height; y++)
        {
            var source = bgra.Slice(y * stride, width * 4);
            var target = result.AsSpan((int)(40 + (long)(height - 1 - y) * rowBytes), width * 3);
            for (var x = 0; x < width; x++)
            {
                int a = source[x * 4 + 3];
                for (var c = 0; c < 3; c++)
                    target[x * 3 + c] = (byte)((source[x * 4 + c] * a + 255 * (255 - a) + 127) / 255);
            }
        }
        return result;
    }

    private static Converted Fail(string error) => new(null, null, error);

    /// Один канал по маске: сдвиг и приведение к 8 битам.
    private readonly struct Channel
    {
        private readonly uint _mask;
        private readonly int _shift;
        private readonly uint _max;

        public Channel(uint mask)
        {
            _mask = mask;
            _shift = mask == 0 ? 0 : System.Numerics.BitOperations.TrailingZeroCount(mask);
            _max = mask == 0 ? 0 : mask >> _shift;
        }

        public byte Read(uint value)
        {
            if (_max == 0)
                return 0;
            var raw = (value & _mask) >> _shift;
            return _max == 255 ? (byte)raw : (byte)((raw * 255 + _max / 2) / _max);
        }
    }
}

/// Простейший кодировщик PNG: 8 бит на канал, RGB или RGBA, без чересстрочности, адаптивный фильтр строк.
public static class Png
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// pixels — строки сверху вниз без выравнивания: RGB (3 байта) или RGBA (4 байта) на пиксель.
    public static byte[] Encode(int width, int height, byte[] pixels, bool alpha)
    {
        var channels = alpha ? 4 : 3;
        var rowBytes = width * channels;
        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = (byte)(alpha ? 6 : 2);
        WriteChunk(output, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                var candidates = new byte[5][];
                for (var f = 0; f < 5; f++)
                    candidates[f] = new byte[rowBytes + 1];
                var zero = new byte[rowBytes];
                for (var y = 0; y < height; y++)
                {
                    var row = pixels.AsSpan(y * rowBytes, rowBytes);
                    var previous = y == 0 ? zero : pixels.AsSpan((y - 1) * rowBytes, rowBytes);
                    var best = 0;
                    var bestScore = long.MaxValue;
                    for (var f = 0; f < 5; f++)
                    {
                        var score = Filter(f, row, previous, channels, candidates[f]);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = f;
                        }
                    }
                    zlib.Write(candidates[best]);
                }
            }
            WriteChunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// Отфильтровать строку (первый байт — тип фильтра) и оценить её: сумма модулей байтов как знаковых.
    private static long Filter(int type, ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, int bpp, byte[] target)
    {
        target[0] = (byte)type;
        long score = 0;
        for (var i = 0; i < row.Length; i++)
        {
            int left = i >= bpp ? row[i - bpp] : 0;
            int up = previous[i];
            int upLeft = i >= bpp ? previous[i - bpp] : 0;
            var predicted = type switch
            {
                1 => left,
                2 => up,
                3 => (left + up) >> 1,
                4 => Paeth(left, up, upLeft),
                _ => 0,
            };
            var value = (byte)(row[i] - predicted);
            target[i + 1] = value;
            score += value < 128 ? value : 256 - value;
        }
        return score;
    }

    internal static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer);
    }

    internal static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
