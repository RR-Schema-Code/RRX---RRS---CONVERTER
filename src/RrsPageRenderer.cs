using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

internal static class RrsPageRenderer
{
    public static int Render(string[] args)
    {
        if (args.Length is < 3 or > 5)
        {
            Console.Error.WriteLine("Usage: RrsToRrx --render-rrs-page <page.xml> <output.png> [scale]");
            return 2;
        }

        string source = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
        double scale = args.Length >= 4 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 1;
        bool geometryOnly = args.Length == 5 && args[4] == "--geometry-only";
        if (scale <= 0 || scale > 4) throw new ArgumentOutOfRangeException(nameof(scale));
        var xml = XDocument.Load(source);
        const double width = 1488, height = 1052;
        var page = new Canvas { Width = width, Height = height, Background = Brushes.White };
        int count = 0, skipped = 0, textCount = 0, connections = 0;
        foreach (var section in xml.Root!.Elements().Where(e => e.Name.LocalName == "D"))
        foreach (var item in section.Elements())
        {
            string xaml = (string?)item.Element("Content") ?? "";
            if (string.IsNullOrWhiteSpace(xaml)) continue;
            double x = Number(item, "Left"), y = Number(item, "Top");
            double w = Number(item, "Width"), h = Number(item, "Height");
            if (w <= 0 || h <= 0) continue;
            try
            {
                if (XamlReader.Parse(xaml) is not UIElement visual) throw new InvalidDataException("Invalid visual");
                // RRS puts the original XAML in a stretched ContentPresenter inside
                // DesignerItem.  Setting Width/Height on the XAML itself changes the
                // layout of rotated Path elements and can hide their strokes.
                var box = new Grid { Width = w, Height = h };
                box.Children.Add(new ContentPresenter
                {
                    Content = visual,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = false
                });
                Canvas.SetLeft(box, x);
                Canvas.SetTop(box, y);
                Canvas.SetZIndex(box, section.Name.LocalName == "NP" ? -1000 : (int)Number(item, "zIndex"));
                page.Children.Add(box);
                count++;

                if (!geometryOnly && (string?)item.Attribute("Funktion") == "Text" && item.Element("Text") is XElement oldText && !string.IsNullOrWhiteSpace(oldText.Value))
                {
                    var label = new TextBlock
                    {
                        Text = oldText.Value,
                        Width = w,
                        Height = h,
                        FontFamily = new FontFamily((string?)oldText.Attribute("Schriftart") ?? "Arial"),
                        FontSize = AttributeNumber(oldText, "Schriftgröße", Math.Clamp(h * .35, 7, 36)),
                        FontStyle = (string?)oldText.Attribute("Style") == "Italic" ? FontStyles.Italic : FontStyles.Normal,
                        FontWeight = (string?)oldText.Attribute("Größe") == "Bold" ? FontWeights.Bold : FontWeights.Normal,
                        TextAlignment = ((string?)oldText.Attribute("Alignment")) switch { "Right" => TextAlignment.Right, "Center" => TextAlignment.Center, _ => TextAlignment.Left },
                        TextWrapping = (string?)oldText.Attribute("Warp") == "Wrap" ? TextWrapping.Wrap : TextWrapping.NoWrap
                    };
                    try { label.Foreground = new BrushConverter().ConvertFromString((string?)oldText.Attribute("Farbe") ?? "#FF000000") as Brush ?? Brushes.Black; }
                    catch { label.Foreground = Brushes.Black; }
                    Canvas.SetLeft(label, x); Canvas.SetTop(label, y);
                    Canvas.SetZIndex(label, (int)Number(item, "zIndex") + 1);
                    page.Children.Add(label);
                    textCount++;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Skipped {(string?)item.Element("ID")}: {ex.Message}");
                skipped++;
            }
        }

        foreach (var route in RrsConnections.Read(xml))
        {
            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = route.Points[0], IsClosed = false };
            figure.Segments.Add(new PolyLineSegment(route.Points.Skip(1), true));
            geometry.Figures.Add(figure);
            var line = new System.Windows.Shapes.Path
            {
                Data = geometry,
                Stroke = new SolidColorBrush(route.Color),
                StrokeThickness = route.Thickness,
                StrokeDashArray = route.Dash == "0" ? new DoubleCollection { 1, 0 } : DoubleCollection.Parse(route.Dash),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Miter,
                SnapsToDevicePixels = true
            };
            Canvas.SetZIndex(line, route.Sequence);
            page.Children.Add(line);
            connections++;
        }

        page.Measure(new Size(width, height));
        page.Arrange(new Rect(0, 0, width, height));
        page.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var stream = File.Create(output)) encoder.Save(stream);
        File.WriteAllText(output + ".json", JsonSerializer.Serialize(new { source, output, count, textCount, connections, skipped, width, height, scale, geometryOnly }, new JsonSerializerOptions { WriteIndented = true }));
        return skipped == 0 ? 0 : 1;
    }

    private static double Number(XElement item, string name) =>
        double.TryParse((string?)item.Element(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : 0;

    private static double AttributeNumber(XElement item, string name, double fallback) =>
        double.TryParse((string?)item.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : fallback;
}
