using DiagramDesigner;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

internal static class RrsConnections
{
    internal sealed record Route(List<Point> Points, Color Color, double Thickness, string Dash, int Sequence);

    internal static IEnumerable<Route> Read(XDocument xml)
    {
        var anchors = xml.Root!.Element("XPoints")?.Elements("Point")
            .Where(p => Guid.TryParse(Value(p, "ID"), out _))
            .ToDictionary(p => Value(p, "ID"), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in xml.Root!.Element("ConnectionsXPoint")?.Elements("Connection") ?? Enumerable.Empty<XElement>())
        {
            if (!anchors.TryGetValue(Value(link, "SourceID"), out XElement? source) ||
                !anchors.TryGetValue(Value(link, "SinkID"), out XElement? sink))
                throw new InvalidDataException("Connection refers to an absent XPoint");

            ConnectorInfo from = Info(source, Value(link, "SourceConnectorName"));
            ConnectorInfo to = Info(sink, Value(link, "SinkConnectorName"));
            List<Point> points = PathFinderX.GetConnectionLine(from, to, true);
            if (points.Count < 2) throw new InvalidDataException("RRS routing returned fewer than two points");
            Color color = (Color)ColorConverter.ConvertFromString(Value(link, "Farbe"))!;
            int style = (int)Number(link, "ArtInt");
            string dash = style == 1 ? "1,3" : "0";
            yield return new Route(points, color, Number(link, "Breite"), dash, (int)Number(link, "zIndex"));
        }
    }

    private static ConnectorInfo Info(XElement item, string connector)
    {
        double x = Number(item, "Left"), y = Number(item, "Top");
        double w = Number(item, "Width"), h = Number(item, "Height");
        ConnectorOrientation orientation = Enum.Parse<ConnectorOrientation>(connector);
        // XPointItem.xaml places 5x5 connectors in RelativePositionPanel.
        // RelativePositionPanel.cs: offset=(itemSize-connectorSize)*relativePosition.
        (double rx, double ry) = orientation switch
        {
            ConnectorOrientation.Top => (0.5, -0.5),
            ConnectorOrientation.Bottom => (0.5, 1.2),
            ConnectorOrientation.Left => (-0.5, 0.5),
            ConnectorOrientation.Right => (1.2, 0.5),
            _ => throw new InvalidDataException("Unsupported connector direction")
        };
        return new ConnectorInfo
        {
            DesignerItemLeft = x, DesignerItemTop = y, DesignerItemSize = new Size(w, h),
            Orientation = orientation,
            Position = new Point(x + (w - 5) * rx + 2.5, y + (h - 5) * ry + 2.5)
        };
    }

    private static string Value(XElement item, string name) => (string?)item.Element(name) ?? "";
    private static double Number(XElement item, string name) =>
        double.TryParse(Value(item, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
}
