using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class PageImageComparer
{
    public static int Compare(string referencePath, string targetPath, string outputPrefix)
    {
        var reference = Read(referencePath);
        var target = Read(targetPath);
        if (reference.Width != target.Width || reference.Height != target.Height)
            throw new InvalidDataException("Page images have different dimensions");

        int width = reference.Width, height = reference.Height;
        byte[] diff = new byte[width * height * 4];
        int referenceInk = 0, targetInk = 0, onlyReference = 0, onlyTarget = 0, colorDifference = 0;
        int[,] regions = new int[3, 3];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            bool a = Ink(reference.Pixels, i), b = Ink(target.Pixels, i);
            if (a) referenceInk++;
            if (b) targetInk++;
            byte red = 255, green = 255, blue = 255;
            if (a && !b) { onlyReference++; red = 220; green = 20; blue = 40; }
            else if (!a && b) { onlyTarget++; red = 25; green = 70; blue = 240; }
            else if (a && b && ColorDistance(reference.Pixels, target.Pixels, i) > 60)
            { colorDifference++; red = 180; green = 0; blue = 180; }
            if (red != 255 || green != 255 || blue != 255) regions[Math.Min(2, y * 3 / height), Math.Min(2, x * 3 / width)]++;
            diff[i] = blue; diff[i + 1] = green; diff[i + 2] = red; diff[i + 3] = 255;
        }

        string prefix = Path.GetFullPath(outputPrefix);
        Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
        string diffPath = prefix + "-diff.png";
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, diff, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(diffPath)) encoder.Save(stream);
        var regionRows = Enumerable.Range(0, 3).Select(y => Enumerable.Range(0, 3).Select(x => regions[y, x]).ToArray()).ToArray();
        var report = new { referencePath, targetPath, width, height, referenceInk, targetInk, onlyReference, onlyTarget, colorDifference, regions = regionRows, legend = "red=only RRS; blue=only RRX; purple=color changed" };
        string reportPath = prefix + "-comparison.json";
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"RRS-only={onlyReference}; RRX-only={onlyTarget}; changed={colorDifference}; diff={diffPath}");
        return 0;
    }

    private static (int Width, int Height, byte[] Pixels) Read(string path)
    {
        using var stream = File.OpenRead(path);
        BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return (converted.PixelWidth, converted.PixelHeight, pixels);
    }

    private static bool Ink(byte[] pixels, int i) => pixels[i] < 225 || pixels[i + 1] < 225 || pixels[i + 2] < 225;
    private static int ColorDistance(byte[] a, byte[] b, int i) =>
        Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
}
