using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public sealed class SpellIconAtlas
{
    private const int SheetSize = 256;
    private const int CellSize = 40;
    private const int CellsPerRow = SheetSize / CellSize;
    private const int CellsPerSheet = CellsPerRow * CellsPerRow;

    private readonly string _sheetDirectory;
    private readonly Dictionary<int, ImageSource> _icons = [];
    private readonly Dictionary<int, byte[]?> _sheets = [];
    private readonly object _gate = new();

    private SpellIconAtlas(string sheetDirectory) => _sheetDirectory = sheetDirectory;

    public static SpellIconAtlas? TryCreate(string installDirectory,
        SpellIconStyle style = SpellIconStyle.Modern)
    {
        foreach (var folder in EnumerateSheetFolders(installDirectory, style))
        {
            if (!Directory.Exists(folder)) continue;
            if (!File.Exists(Path.Combine(folder, "Spells01.tga"))) continue;
            return new SpellIconAtlas(folder);
        }
        return null;
    }

    private static IEnumerable<string> EnumerateSheetFolders(string installDirectory, SpellIconStyle style)
    {
        var modern = Path.Combine(installDirectory, "uifiles", "default_modern");
        var classic = Path.Combine(installDirectory, "uifiles", "default");
        if (style == SpellIconStyle.Modern)
        {
            yield return modern;
            yield return classic;
        }
        else
        {
            yield return classic;
            yield return modern;
        }
    }

    public ImageSource? GetIcon(int iconId)
    {
        if (iconId <= 0) return null;
        lock (_gate)
        {
            if (_icons.TryGetValue(iconId, out var cached)) return cached;
            var created = CreateIcon(iconId);
            if (created is not null) _icons[iconId] = created;
            return created;
        }
    }

    public static ImageSource GenericIcon { get; } = CreateGenericIcon();

    private static ImageSource CreateGenericIcon()
    {
        const int size = 40;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var edge = x < 2 || y < 2 || x >= size - 2 || y >= size - 2;
            var diamond = Math.Abs(x - 19) + Math.Abs(y - 19) is >= 8 and <= 12;
            var offset = (y * size + x) * 4;
            if (edge)
            {
                pixels[offset] = 0x6A;
                pixels[offset + 1] = 0x8C;
                pixels[offset + 2] = 0xA2;
                pixels[offset + 3] = 255;
            }
            else if (diamond)
            {
                pixels[offset] = 0xB8;
                pixels[offset + 1] = 0xD4;
                pixels[offset + 2] = 0xE5;
                pixels[offset + 3] = 255;
            }
            else
            {
                pixels[offset] = 0x2B;
                pixels[offset + 1] = 0x3F;
                pixels[offset + 2] = 0x52;
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private ImageSource? CreateIcon(int iconId)
    {
        var sheetIndex = iconId / CellsPerSheet;
        var cellIndex = iconId % CellsPerSheet;
        var pixels = LoadSheet(sheetIndex);
        if (pixels is null) return null;

        var cellX = cellIndex % CellsPerRow;
        var cellY = cellIndex / CellsPerRow;
        var crop = new byte[CellSize * CellSize * 4];
        for (var row = 0; row < CellSize; row++)
        {
            var sourceOffset = ((cellY * CellSize + row) * SheetSize + cellX * CellSize) * 4;
            var destinationOffset = row * CellSize * 4;
            Buffer.BlockCopy(pixels, sourceOffset, crop, destinationOffset, CellSize * 4);
        }

        var bitmap = BitmapSource.Create(CellSize, CellSize, 96, 96, PixelFormats.Bgra32, null, crop,
            CellSize * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private byte[]? LoadSheet(int sheetIndex)
    {
        if (_sheets.TryGetValue(sheetIndex, out var cached)) return cached;
        var path = Path.Combine(_sheetDirectory, $"Spells{sheetIndex + 1:00}.tga");
        byte[]? pixels = null;
        try
        {
            if (File.Exists(path)) pixels = TgaReader.ReadBgra32(path, SheetSize, SheetSize);
        }
        catch (IOException)
        {
            pixels = null;
        }
        catch (UnauthorizedAccessException)
        {
            pixels = null;
        }
        catch (InvalidDataException)
        {
            pixels = null;
        }

        _sheets[sheetIndex] = pixels;
        return pixels;
    }
}

internal static class TgaReader
{
    public static byte[] ReadBgra32(string path, int expectedWidth, int expectedHeight)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 18) throw new InvalidDataException("TGA header is truncated.");

        var idLength = data[0];
        var colorMapType = data[1];
        var imageType = data[2];
        var width = data[12] | (data[13] << 8);
        var height = data[14] | (data[15] << 8);
        var depth = data[16];
        var descriptor = data[17];
        if (colorMapType != 0) throw new InvalidDataException("Color-mapped TGA is not supported.");
        if (width != expectedWidth || height != expectedHeight)
            throw new InvalidDataException("Unexpected TGA dimensions.");
        if (depth is not (24 or 32)) throw new InvalidDataException("Only 24/32-bit TGA is supported.");

        var offset = 18 + idLength;
        var topOrigin = (descriptor & 0x20) != 0;
        var pixels = imageType switch
        {
            2 => ReadUncompressed(data, offset, width, height, depth),
            10 => ReadRle(data, offset, width, height, depth),
            _ => throw new InvalidDataException($"Unsupported TGA image type {imageType}.")
        };

        if (!topOrigin) FlipVertically(pixels, width, height);
        return pixels;
    }

    private static byte[] ReadUncompressed(byte[] data, int offset, int width, int height, int depth)
    {
        var bytesPerPixel = depth / 8;
        var required = checked(offset + width * height * bytesPerPixel);
        if (data.Length < required) throw new InvalidDataException("TGA pixel data is truncated.");

        var pixels = new byte[width * height * 4];
        var source = offset;
        for (var index = 0; index < width * height; index++)
        {
            var destination = index * 4;
            pixels[destination] = data[source];
            pixels[destination + 1] = data[source + 1];
            pixels[destination + 2] = data[source + 2];
            pixels[destination + 3] = depth == 32 ? data[source + 3] : (byte)255;
            source += bytesPerPixel;
        }
        return pixels;
    }

    private static byte[] ReadRle(byte[] data, int offset, int width, int height, int depth)
    {
        var bytesPerPixel = depth / 8;
        var pixels = new byte[width * height * 4];
        var source = offset;
        var destinationPixel = 0;
        var total = width * height;
        while (destinationPixel < total)
        {
            if (source >= data.Length) throw new InvalidDataException("TGA RLE data is truncated.");
            var packet = data[source++];
            var count = (packet & 0x7F) + 1;
            if ((packet & 0x80) != 0)
            {
                if (source + bytesPerPixel > data.Length)
                    throw new InvalidDataException("TGA RLE data is truncated.");
                var b = data[source];
                var g = data[source + 1];
                var r = data[source + 2];
                var a = depth == 32 ? data[source + 3] : (byte)255;
                source += bytesPerPixel;
                for (var index = 0; index < count; index++)
                {
                    if (destinationPixel >= total) throw new InvalidDataException("TGA RLE overflow.");
                    var destination = destinationPixel++ * 4;
                    pixels[destination] = b;
                    pixels[destination + 1] = g;
                    pixels[destination + 2] = r;
                    pixels[destination + 3] = a;
                }
            }
            else
            {
                for (var index = 0; index < count; index++)
                {
                    if (destinationPixel >= total || source + bytesPerPixel > data.Length)
                        throw new InvalidDataException("TGA RLE data is truncated.");
                    var destination = destinationPixel++ * 4;
                    pixels[destination] = data[source];
                    pixels[destination + 1] = data[source + 1];
                    pixels[destination + 2] = data[source + 2];
                    pixels[destination + 3] = depth == 32 ? data[source + 3] : (byte)255;
                    source += bytesPerPixel;
                }
            }
        }
        return pixels;
    }

    private static void FlipVertically(byte[] pixels, int width, int height)
    {
        var stride = width * 4;
        var row = new byte[stride];
        for (var top = 0; top < height / 2; top++)
        {
            var bottom = height - 1 - top;
            var topOffset = top * stride;
            var bottomOffset = bottom * stride;
            Buffer.BlockCopy(pixels, topOffset, row, 0, stride);
            Buffer.BlockCopy(pixels, bottomOffset, pixels, topOffset, stride);
            Buffer.BlockCopy(row, 0, pixels, bottomOffset, stride);
        }
    }
}
