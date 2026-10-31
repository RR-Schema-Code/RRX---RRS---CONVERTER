using System.Windows;

namespace DiagramDesigner;

// These two types are the data contract used by the linked RRS PathFinderX.cs.
internal struct ConnectorInfo
{
    public double DesignerItemLeft { get; set; }
    public double DesignerItemTop { get; set; }
    public Size DesignerItemSize { get; set; }
    public Point Position { get; set; }
    public ConnectorOrientation Orientation { get; set; }
}

public enum ConnectorOrientation { None, Left, Top, Right, Bottom }
