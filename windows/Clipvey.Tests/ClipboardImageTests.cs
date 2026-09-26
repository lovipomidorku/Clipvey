using System.Buffers.Binary;
using System.IO.Compression;
using Clipvey.Core;

namespace Clipvey.Tests;

/// Картинки в буфере Windows: DIB → PNG, пиксели → CF_DIB, правило «текст или картинка».
public class ClipboardImageTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("  \r\n", true)]
    [InlineData("https://example.com/cat.png", true)]
    [InlineData("  https://example.com/cat.png\n", true)]
    [InlineData("data:image/png;base64,AAAA", true)]
    [InlineData("кот", false)]
    [InlineData("https://example.com/a https://example.com/b", false)]
    [InlineData("Итого\t42", false)]
    [InlineData("example.com", false)]
    public void TextIsAuxiliary(string? text, bool expected) => Assert.Equal(expected, ClipboardRules.TextIsAuxiliary(text));

    // Картинка 2×2 сверху вниз: красный, зелёный / синий, белый.
    private static readonly byte[][] Rgb = [[255, 0, 0], [0, 255, 0], [0, 0, 255], [255, 255, 255]];

    [Fact]
    public void Dib24BottomUp()
    {
        // Строки снизу вверх, BGR, выравнивание до 4 байт (2×3 = 6 → 8).
        var dib = Header(40, 2, 2, 24, 0);
        dib.AddRange([255, 0, 0, 255, 255, 255, 0, 0]); // нижняя строка: синий, белый
        dib.AddRange([0, 0, 255, 0, 255, 0, 0, 0]); // верхняя: красный, зелёный
        var image = Decode(Convert(dib));
        Assert.Equal(2, image.ColorType);
        AssertPixels(image, Rgb, alpha: null);
    }

    [Fact]
    public void Dib32TopDownIsOpaque()
    {
        // BI_RGB 32 бита: четвёртый байт не альфа, даже если не нулевой.
        var dib = Header(40, 2, -2, 32, 0);
        dib.AddRange([0, 0, 255, 7, 0, 255, 0, 7]);
        dib.AddRange([255, 0, 0, 7, 255, 255, 255, 7]);
        var image = Decode(Convert(dib));
        Assert.Equal(2, image.ColorType);
        AssertPixels(image, Rgb, alpha: null);
    }

    [Fact]
    public void DibV5BitfieldsKeepsAlpha()
    {
        var dib = Header(124, 2, 2, 32, 3);
        Masks(dib, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000);
        dib.AddRange([255, 0, 0, 255, 255, 255, 255, 0]); // синий непрозрачный, белый прозрачный
        dib.AddRange([0, 0, 255, 128, 0, 255, 0, 255]); // красный полупрозрачный, зелёный
        var image = Decode(Convert(dib));
        Assert.Equal(6, image.ColorType);
        AssertPixels(image, Rgb, alpha: [128, 255, 255, 0]);
    }

    [Fact]
    public void DeclaredAlphaAllZeroBecomesOpaque()
    {
        var dib = Header(124, 2, 2, 32, 3);
        Masks(dib, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000);
        dib.AddRange([255, 0, 0, 0, 255, 255, 255, 0]);
        dib.AddRange([0, 0, 255, 0, 0, 255, 0, 0]);
        var image = Decode(Convert(dib));
        AssertPixels(image, Rgb, alpha: [255, 255, 255, 255]);
    }

    [Fact]
    public void Dib40BitfieldsMasksAfterHeader()
    {
        // 40-байтный заголовок с BI_BITFIELDS: три маски идут сразу за ним, пиксели — после масок.
        var dib = Header(40, 2, 2, 32, 3);
        dib.AddRange(Le(0x000000FF)); // красный — младший байт (RGBX)
        dib.AddRange(Le(0x0000FF00));
        dib.AddRange(Le(0x00FF0000));
        dib.AddRange([0, 0, 255, 0, 255, 255, 255, 0]);
        dib.AddRange([255, 0, 0, 0, 0, 255, 0, 0]);
        var image = Decode(Convert(dib));
        Assert.Equal(2, image.ColorType);
        AssertPixels(image, Rgb, alpha: null);
    }

    [Fact]
    public void Dib16Bit555()
    {
        var dib = Header(40, 2, -2, 16, 0);
        dib.AddRange([0x00, 0x7C, 0xE0, 0x03]); // красный, зелёный
        dib.AddRange([0x1F, 0x00, 0xFF, 0x7F]); // синий, белый
        AssertPixels(Decode(Convert([.. dib])), Rgb, alpha: null);
    }

    [Fact]
    public void Dib8BitPalette()
    {
        var dib = Header(40, 2, -2, 8, 0, colorsUsed: 4);
        dib.AddRange([0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]); // BGRX: красный, зелёный, синий, белый
        dib.AddRange([0, 1, 0, 0]);
        dib.AddRange([2, 3, 0, 0]);
        AssertPixels(Decode(Convert([.. dib])), Rgb, alpha: null);
    }

    [Fact]
    public void Dib1BitPalette()
    {
        var dib = Header(40, 2, -2, 1, 0);
        dib.AddRange([0, 0, 0, 0, 255, 255, 255, 0]);
        dib.AddRange([0b0100_0000, 0, 0, 0]); // чёрный, белый
        dib.AddRange([0b1000_0000, 0, 0, 0]); // белый, чёрный
        AssertPixels(Decode(Convert(dib)), [[0, 0, 0], [255, 255, 255], [255, 255, 255], [0, 0, 0]], alpha: null);
    }

    [Fact]
    public void ColorsUsedSkippedAt24Bit()
    {
        // biClrUsed > 0 при 24 битах: палитра есть и пропускается.
        var dib = Header(40, 2, 2, 24, 0, colorsUsed: 2);
        dib.AddRange([9, 9, 9, 0, 9, 9, 9, 0]);
        dib.AddRange([255, 0, 0, 255, 255, 255, 0, 0]);
        dib.AddRange([0, 0, 255, 0, 255, 0, 0, 0]);
        AssertPixels(Decode(Convert([.. dib])), Rgb, alpha: null);
    }

    [Fact]
    public void BiPngPassesThrough()
    {
        var png = Png.Encode(1, 1, [1, 2, 3], alpha: false);
        var dib = Header(40, 1, 1, 0, 5, sizeImage: (uint)png.Length);
        dib.AddRange(png);
        var result = Dib.ToImage(dib.ToArray());
        Assert.Equal("image/png", result.Mime);
        Assert.Equal(png, result.Data);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(47)]
    public void TruncatedIsRejected(int length)
    {
        var dib = Header(40, 2, 2, 24, 0);
        dib.AddRange(new byte[16]);
        var result = Dib.ToImage(dib.Take(length).ToArray());
        Assert.Null(result.Data);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void RleIsRejected()
    {
        var dib = Header(40, 2, 2, 8, 1);
        dib.AddRange(new byte[1100]);
        Assert.Null(Dib.ToImage(dib.ToArray()).Data);
    }

    [Fact]
    public void FromBgraBlendsWithWhite()
    {
        // 2×1: красный непрозрачный, чёрный полностью прозрачный → белый. Строка дополнена до 8 байт.
        byte[] bgra = [0, 0, 255, 255, 0, 0, 0, 0];
        var dib = Dib.FromBgra(2, 1, bgra, 8);
        Assert.Equal(40u, BinaryPrimitives.ReadUInt32LittleEndian(dib));
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14)));
        Assert.Equal(48, dib.Length);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 255, 0, 0 }, dib[40..]);
    }

    [Fact]
    public void FromBgraRoundTrip()
    {
        // Пиксели → CF_DIB → PNG: снизу вверх и обратно.
        byte[] bgra = [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255];
        var dib = Dib.FromBgra(2, 2, bgra, 8);
        AssertPixels(Decode(Convert([.. dib])), Rgb, alpha: null);
    }

    [Fact]
    public void PngLargeImageDecodes()
    {
        // Большая картинка с градиентом: проверка всех фильтров строк и CRC.
        const int width = 300, height = 200;
        var pixels = new byte[width * height * 4];
        var random = new Random(1);
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = i % 7 == 0 ? (byte)random.Next(256) : (byte)(i / 13 % 256);
        var image = Decode(Png.Encode(width, height, pixels, alpha: true));
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
        Assert.Equal(pixels, image.Pixels);
    }

    // MARK: - Вспомогательное

    private static byte[] Convert(List<byte> dib)
    {
        var result = Dib.ToImage(dib.ToArray());
        Assert.Null(result.Error);
        Assert.Equal("image/png", result.Mime);
        return result.Data!;
    }

    private static List<byte> Header(int size, int width, int height, int bits, uint compression, uint colorsUsed = 0, uint sizeImage = 0)
    {
        var header = new byte[size];
        BinaryPrimitives.WriteInt32LittleEndian(header, size);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)bits);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), sizeImage);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), colorsUsed);
        return [.. header];
    }

    private static void Masks(List<byte> dib, uint red, uint green, uint blue, uint alpha)
    {
        var masks = new[] { red, green, blue, alpha };
        for (var i = 0; i < 4; i++)
        {
            var bytes = Le(masks[i]);
            for (var b = 0; b < 4; b++)
                dib[40 + i * 4 + b] = bytes[b];
        }
    }

    private static byte[] Le(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static void AssertPixels(DecodedPng image, byte[][] rgb, byte[]? alpha)
    {
        Assert.Equal(2, image.Width);
        var channels = image.ColorType == 6 ? 4 : 3;
        Assert.Equal(alpha is null ? 3 : 4, channels);
        for (var i = 0; i < rgb.Length; i++)
        {
            Assert.Equal(rgb[i], image.Pixels.AsSpan(i * channels, 3).ToArray());
            if (alpha is not null)
                Assert.Equal(alpha[i], image.Pixels[i * channels + 3]);
        }
    }

    private sealed record DecodedPng(int Width, int Height, int ColorType, byte[] Pixels);

    /// Разбор PNG ровно того вида, что пишет Png.Encode (8 бит, RGB/RGBA, без чересстрочности), с проверкой CRC.
    private static DecodedPng Decode(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        var at = 8;
        int width = 0, height = 0, colorType = 0;
        using var idat = new MemoryStream();
        while (true)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length));
            Assert.Equal(crc, Png.Crc(0xFFFFFFFF, png.AsSpan(at + 4, 4 + length)) ^ 0xFFFFFFFF);
            at += 12 + length;
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                Assert.Equal(8, data[8]);
                colorType = data[9];
                Assert.Equal(0, data[12]);
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }
            else if (type == "IEND")
            {
                break;
            }
        }
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var bytes = raw.ToArray();
        var bpp = colorType == 6 ? 4 : 3;
        var rowBytes = width * bpp;
        Assert.Equal((rowBytes + 1) * height, bytes.Length);
        var pixels = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            var filter = bytes[y * (rowBytes + 1)];
            for (var i = 0; i < rowBytes; i++)
            {
                int value = bytes[y * (rowBytes + 1) + 1 + i];
                int left = i >= bpp ? pixels[y * rowBytes + i - bpp] : 0;
                int up = y > 0 ? pixels[(y - 1) * rowBytes + i] : 0;
                int upLeft = i >= bpp && y > 0 ? pixels[(y - 1) * rowBytes + i - bpp] : 0;
                value += filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) >> 1,
                    4 => Png.Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException($"фильтр {filter}"),
                };
                pixels[y * rowBytes + i] = (byte)value;
            }
        }
        return new DecodedPng(width, height, colorType, pixels);
    }
}
