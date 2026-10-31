using System.Globalization;
using System.Text;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;

// Converts RRS DrawingImage XAML into actual SVG path data for RRX's xSvg element.
// Unsupported WPF effects stay on the existing PNG path.
internal static class WpfSvgConverter
{
    static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    static string F(double n) => n.ToString("0.########", CultureInfo.InvariantCulture);

    public static bool TryConvert(string xaml, double width, double height, out string base64, out double angle)
    {
        base64 = "";
        angle = 0;
        try
        {
            if (XamlReader.Parse(xaml) is not Viewbox box || box.Child is not Image image
                || image.Source is not DrawingImage source || source.Drawing is null
                || box.RenderTransform is Transform rt && !rt.Value.IsIdentity)
                return false;
            if (box.LayoutTransform is RotateTransform rotate) angle = rotate.Angle;
            else if (box.LayoutTransform is Transform lt && !lt.Value.IsIdentity) return false;
            if (Math.Abs(angle / 90 - Math.Round(angle / 90)) > 0.001) return false;
            bool quarterTurn = ((int)Math.Round(Math.Abs(angle) / 90)) % 2 != 0;
            double drawingWidth = quarterTurn ? height : width;
            double drawingHeight = quarterTurn ? width : height;
            var bounds = source.Drawing.Bounds;
            if (bounds.IsEmpty || bounds.Width < 0.001 || bounds.Height < 0.001) return false;
            var svg = new XElement(Svg + "svg",
                new XAttribute("viewBox", $"0 0 {F(drawingWidth)} {F(drawingHeight)}"),
                new XAttribute("width", F(drawingWidth)), new XAttribute("height", F(drawingHeight)),
                new XAttribute("overflow", "hidden"));
            double sx = drawingWidth / bounds.Width, sy = drawingHeight / bounds.Height;
            var mapped = new XElement(Svg + "g", new XAttribute("transform",
                $"matrix({F(sx)} 0 0 {F(sy)} {F(-bounds.Left * sx)} {F(-bounds.Top * sy)})"));
            if (!AppendDrawing(source.Drawing, mapped)) return false;
            svg.Add(mapped);
            base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg.ToString(SaveOptions.DisableFormatting)));
            return true;
        }
        catch { return false; }
    }

    static bool AppendDrawing(Drawing drawing, XElement parent)
    {
        if (drawing is DrawingGroup group)
        {
            if (group.OpacityMask is not null) return false;
            var node = new XElement(Svg + "g");
            if (group.Opacity < 0.999) node.SetAttributeValue("opacity", F(group.Opacity));
            if (group.Transform is Transform transform && !transform.Value.IsIdentity)
            {
                var m = transform.Value;
                node.SetAttributeValue("transform", $"matrix({F(m.M11)} {F(m.M12)} {F(m.M21)} {F(m.M22)} {F(m.OffsetX)} {F(m.OffsetY)})");
            }
            foreach (var child in group.Children)
                if (!AppendDrawing(child, node)) return false;
            parent.Add(node);
            return true;
        }
        if (drawing is not GeometryDrawing geometryDrawing || geometryDrawing.Geometry is null) return false;
        string pathData = PathGeometry.CreateFromGeometry(geometryDrawing.Geometry).ToString(CultureInfo.InvariantCulture);
        if (pathData.StartsWith("F0", StringComparison.Ordinal) || pathData.StartsWith("F1", StringComparison.Ordinal))
            pathData = pathData[2..];
        if (string.IsNullOrWhiteSpace(pathData)) return false;
        var path = new XElement(Svg + "path", new XAttribute("d", pathData));
        if (geometryDrawing.Geometry is PathGeometry pg && pg.FillRule == FillRule.EvenOdd)
            path.SetAttributeValue("fill-rule", "evenodd");
        if (!SetBrush(path, "fill", geometryDrawing.Brush)) return false;
        if (geometryDrawing.Pen is Pen pen)
        {
            if (!SetBrush(path, "stroke", pen.Brush)) return false;
            path.SetAttributeValue("stroke-width", F(pen.Thickness));
            path.SetAttributeValue("stroke-linejoin", pen.LineJoin.ToString().ToLowerInvariant());
            path.SetAttributeValue("stroke-linecap", pen.StartLineCap.ToString().ToLowerInvariant());
            if (pen.DashStyle is DashStyle dash && dash.Dashes.Count > 0)
                path.SetAttributeValue("stroke-dasharray", string.Join(" ", dash.Dashes.Select(n => F(n * pen.Thickness))));
        }
        else path.SetAttributeValue("stroke", "none");
        parent.Add(path);
        return true;
    }

    static bool SetBrush(XElement element, string name, Brush? brush)
    {
        if (brush is null) { element.SetAttributeValue(name, "none"); return true; }
        if (brush is not SolidColorBrush solid || solid.Transform is Transform t && !t.Value.IsIdentity
            || solid.RelativeTransform is Transform relative && !relative.Value.IsIdentity)
            return false;
        var color = solid.Color;
        element.SetAttributeValue(name, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
        double opacity = solid.Opacity * color.A / 255.0;
        if (opacity < 0.999) element.SetAttributeValue(name + "-opacity", F(opacity));
        return true;
    }
}
