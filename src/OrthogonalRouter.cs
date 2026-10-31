using System.Windows;

namespace DiagramDesigner;

// Independent fallback for XPoint connections when the RRS source is absent.
// It preserves the connector endpoints and chooses a short orthogonal route.
internal static class PathFinderX
{
    internal static List<Point> GetConnectionLine(ConnectorInfo source, ConnectorInfo sink, bool showLastLine)
    {
        Point start = source.Position;
        Point end = sink.Position;
        Point from = MoveOut(start, source.Orientation);
        Point to = MoveOut(end, sink.Orientation);
        Rect sourceBounds = Bounds(source);
        Rect sinkBounds = Bounds(sink);
        sourceBounds.Inflate(3, 3);
        sinkBounds.Inflate(3, 3);

        double left = Math.Min(sourceBounds.Left, sinkBounds.Left) - 12;
        double right = Math.Max(sourceBounds.Right, sinkBounds.Right) + 12;
        double top = Math.Min(sourceBounds.Top, sinkBounds.Top) - 12;
        double bottom = Math.Max(sourceBounds.Bottom, sinkBounds.Bottom) + 12;
        double middleX = (from.X + to.X) / 2;
        double middleY = (from.Y + to.Y) / 2;

        var candidates = new List<List<Point>>
        {
            new() { from, to },
            new() { from, new Point(from.X, to.Y), to },
            new() { from, new Point(to.X, from.Y), to },
            new() { from, new Point(middleX, from.Y), new Point(middleX, to.Y), to },
            new() { from, new Point(from.X, middleY), new Point(to.X, middleY), to },
            new() { from, new Point(left, from.Y), new Point(left, to.Y), to },
            new() { from, new Point(right, from.Y), new Point(right, to.Y), to },
            new() { from, new Point(from.X, top), new Point(to.X, top), to },
            new() { from, new Point(from.X, bottom), new Point(to.X, bottom), to }
        };

        List<Point> route = candidates.OrderBy(path => Score(path, sourceBounds, sinkBounds)).First();
        var result = new List<Point> { start };
        result.AddRange(route);
        result.Add(end);
        return Simplify(result);
    }

    private static Point MoveOut(Point point, ConnectorOrientation orientation) => orientation switch
    {
        ConnectorOrientation.Left => new Point(point.X - 12, point.Y),
        ConnectorOrientation.Right => new Point(point.X + 12, point.Y),
        ConnectorOrientation.Top => new Point(point.X, point.Y - 12),
        ConnectorOrientation.Bottom => new Point(point.X, point.Y + 12),
        _ => point
    };

    private static Rect Bounds(ConnectorInfo connector) => new(
        connector.DesignerItemLeft, connector.DesignerItemTop,
        Math.Max(0, connector.DesignerItemSize.Width), Math.Max(0, connector.DesignerItemSize.Height));

    private static double Score(List<Point> path, Rect source, Rect sink)
    {
        double score = 0;
        for (int i = 1; i < path.Count; i++)
        {
            Point a = path[i - 1], b = path[i];
            if (a.X != b.X && a.Y != b.Y) return double.PositiveInfinity;
            score += Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
            if (i > 1 && a != b) score += 8;
            if (CrossesInterior(a, b, source) || CrossesInterior(a, b, sink)) score += 100000;
        }
        return score;
    }

    private static bool CrossesInterior(Point a, Point b, Rect r)
    {
        if (a.X == b.X)
            return a.X > r.Left && a.X < r.Right &&
                Math.Max(Math.Min(a.Y, b.Y), r.Top) < Math.Min(Math.Max(a.Y, b.Y), r.Bottom);
        if (a.Y == b.Y)
            return a.Y > r.Top && a.Y < r.Bottom &&
                Math.Max(Math.Min(a.X, b.X), r.Left) < Math.Min(Math.Max(a.X, b.X), r.Right);
        return true;
    }

    private static List<Point> Simplify(List<Point> points)
    {
        var result = new List<Point>();
        foreach (Point point in points)
        {
            if (result.Count > 0 && result[^1] == point) continue;
            result.Add(point);
            while (result.Count >= 3)
            {
                Point a = result[^3], b = result[^2], c = result[^1];
                if ((a.X == b.X && b.X == c.X) || (a.Y == b.Y && b.Y == c.Y))
                    result.RemoveAt(result.Count - 2);
                else break;
            }
        }
        return result;
    }
}
