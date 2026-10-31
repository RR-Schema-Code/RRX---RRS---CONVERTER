using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

internal static class Program
{
    const string Zero = "00000000-0000-0000-0000-000000000000";
    static readonly Dictionary<string, int> Count = new();
    static readonly List<string> Warnings = new();
    static readonly Dictionary<string, string> ImageCache = new();
    static SqliteConnection target = null!;
    static SqliteTransaction tx = null!;
    static readonly Dictionary<string, Writer> writers = new();

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--render-rrs-page")
            return RrsPageRenderer.Render(args);
        if (args.Length == 4 && args[0] == "--compare-pages")
            return PageImageComparer.Compare(args[1], args[2], args[3]);
        if (args.Length != 3) { Console.Error.WriteLine("RrsToRrx <RRS folder> <Dummy.rrxp> <output.rrxp>"); return 2; }
        string source = Path.GetFullPath(args[0]), dummy = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
        if (!Directory.Exists(source) || !File.Exists(dummy)) throw new FileNotFoundException("Source or Dummy.rrxp missing");
        if (output.Equals(dummy, StringComparison.OrdinalIgnoreCase) || output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must be a separate file outside the source");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.Copy(dummy, output, true);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = output }.ToString());
        target = db; db.Open();
        using var transaction = db.BeginTransaction(); tx = transaction;
        foreach (string table in new[] { "items", "elements_normal", "components", "datapoints", "wires", "articles", "additionalarticles", "logs" })
            Exec($"DELETE FROM \"{table}\"");
        string projectFile = Path.Combine(source, "Projekt.xml");
        var projectXml = File.Exists(projectFile) ? XDocument.Load(projectFile) : null;
        var ispHeaders = projectXml?.Descendants("ISP")
            .Where(e => Guid.TryParse(Attribute(e, "ID"), out _))
            .ToDictionary(e => Attribute(e, "ID"), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        var orderedIspDirs = ispHeaders.Keys
            .Select(id => Path.Combine(source, id)).Where(Directory.Exists)
            .Concat(Directory.GetDirectories(source).Where(dir => !ispHeaders.ContainsKey(Path.GetFileName(dir))).OrderBy(x => x));
        string projectId = Guid.NewGuid().ToString();
        string projectName = projectXml?.Descendants("Projekt").FirstOrDefault() is XElement projectNode
            ? Attribute(projectNode, "Name") : Path.GetFileName(source);
        if (string.IsNullOrWhiteSpace(projectName)) projectName = Path.GetFileName(source);
        var root = Item("xProject", Zero, projectId, projectName, "");
        root["x00"] = projectName; root["x32"] = projectName;
        if (ispHeaders.Values.FirstOrDefault() is XElement firstHeader)
        {
            ApplyHeader(root, firstHeader);
            root["x33"] = projectName;
        }
        string logo = Path.Combine(source, "Logo.png");
        if (File.Exists(logo)) root["x49"] = Convert.ToBase64String(File.ReadAllBytes(logo));
        Add("items", root);
        foreach (string ispDir in orderedIspDirs)
        {
            string ispId = Path.GetFileName(ispDir);
            if (!Guid.TryParse(ispId, out _)) continue;
            string pagesFile = Path.Combine(ispDir, "Seiten.xml"), oldDbFile = Path.Combine(ispDir, "Datenbank.db3");
            using var oldDb = File.Exists(oldDbFile)
                ? new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = oldDbFile, Mode = SqliteOpenMode.ReadOnly }.ToString())
                : null;
            oldDb?.Open();
            if (oldDb == null) Warn($"Missing Datenbank.db3: {ispId}; drawings only");
            ispHeaders.TryGetValue(ispId, out XElement? header);
            string ispName = header == null ? oldDb == null ? ispId : IspName(oldDb, ispId) : Attribute(header, "Name");
            string ispDescription = header == null ? "" : Attribute(header, "Beschreibung");
            var asp = Item("xAsp", projectId, ispId, ispName, ispDescription);
            asp["x00"] = header == null ? ispName : Attribute(header, "BMK");
            asp["x01"] = header == null ? "" : Attribute(header, "VorBMK");
            asp["x02"] = "1";
            if (header != null) ApplyHeader(asp, header);
            Add("items", asp);
            var tree = File.Exists(pagesFile) ? XDocument.Load(pagesFile) : RecoveredPageTree(ispDir, ispId);
            int total = tree.Descendants().Count(e => e.Name.LocalName is "BlattD" or "BlattA");
            foreach (var folder in tree.Root!.Element("ISP")!.Elements())
            {
                string folderType = folder.Name.LocalName;
                string folderId = Attribute(folder, "ID");
                if (folderType is "Verzeichnis" or "Anlage")
                {
                    if (!Guid.TryParse(folderId, out _)) folderId = Guid.NewGuid().ToString();
                    folderId = StableId($"folder|{ispId}|{folderId}");
                    var f = Item(folderType == "Anlage" ? "xSystemFolder" : "xIndexFolder", ispId, folderId,
                        Attribute(folder, "Name"), Attribute(folder, "Beschreibung"));
                    f["x00"] = folderType == "Anlage" ? Attribute(folder, "BMK") : Attribute(folder, "Name");
                    f["x02"] = "1"; f["x20"] = Attribute(folder, "Gewerk"); Add("items", f);
                }
                foreach (var page in folder.Elements())
                {
                    string name = page.Name.LocalName;
                    if (name is not ("BlattD" or "Blatt" or "BlattA")) continue; // Funktionsliste omitted.
                    if (name == "Blatt") { CountOne("index_pages_omitted"); continue; }
                    string oldPageId = Attribute(page, "ID");
                    if (!Guid.TryParse(oldPageId, out _)) { Warn($"Invalid page ID in {ispId}"); continue; }
                    string pageId = PageId(ispId, oldPageId);
                    string type = name switch { "BlattD" => "xCover", "Blatt" => "xIndexPage", _ => "xSystemPage" };
                    var row = Item(type, type == "xCover" ? ispId : folderId, pageId, Attribute(page, "Name"), Attribute(page, "Beschreibung"));
                    row["x02"] = "1"; row["x03"] = Attribute(page, "Seite"); row["x04"] = total.ToString(CultureInfo.InvariantCulture);
                    if (type == "xIndexPage") row["x05"] = "1";
                    Add("items", row);
                    string drawing = Path.Combine(ispDir, oldPageId + ".xml");
                    if (name == "BlattD") { CountOne("blank_covers"); continue; }
                    if (File.Exists(drawing)) Drawing(drawing, pageId, ispId, oldPageId);
                    else Warn($"Missing drawing: {drawing}");
                }
            }
            if (oldDb != null) Data(oldDb, ispId);
            Console.WriteLine($"{ispName}: {total} pages");
        }
        transaction.Commit();
        foreach (var writer in writers.Values) writer.Dispose();
        using (var check = db.CreateCommand()) { check.CommandText = "PRAGMA integrity_check"; if (check.ExecuteScalar()?.ToString() != "ok") throw new InvalidDataException("SQLite integrity check failed"); }
        string report = Path.ChangeExtension(output, ".report.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { source, dummy, output, counts = Count, warnings = Warnings }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        Console.WriteLine(JsonSerializer.Serialize(Count)); Console.WriteLine($"Warnings={Warnings.Count}; report={report}");
        return 0;
    }

    static XDocument RecoveredPageTree(string ispDir, string ispId)
    {
        var pageIds = Directory.EnumerateFiles(ispDir, "*.xml")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(id => Guid.TryParse(id, out _) &&
                !id!.Equals("11111111-1111-1111-1111-111111111111", StringComparison.OrdinalIgnoreCase))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Warn($"Missing Seiten.xml: {ispId}; reconstructed {pageIds.Count} drawing pages with neutral names");
        var folder = new XElement("Anlage",
            new XAttribute("ID", StableId($"recovered-folder|{ispId}")),
            new XAttribute("Name", "Wiederhergestellte Blätter"),
            new XAttribute("Beschreibung", "Seitenliste fehlt in RRS"));
        for (int i = 0; i < pageIds.Count; i++)
            folder.Add(new XElement("BlattA",
                new XAttribute("ID", pageIds[i]!),
                new XAttribute("Name", $"Blatt {i + 1:00}"),
                new XAttribute("Seite", (i + 1).ToString(CultureInfo.InvariantCulture))));
        return new XDocument(new XElement("Root", new XElement("ISP", folder)));
    }

    static void Drawing(string file, string pageId, string ispId, string oldPageId)
    {
        var xml = XDocument.Load(file);
        // RRS sheets use a 1488 x 1052 canvas. NP is the old Normblatt.
        // DaP contains border anchors; its connected points become component docks.
        const int width = 1488, height = 1052;
        var docks = ReadComponentDocks(xml);
        // RRX paints elements in database insertion order. RRS instead uses
        // Canvas.ZIndex, with source insertion order breaking equal-Z ties.
        // Collect one page first and write it in that exact visual order.
        var pageRows = new List<Dictionary<string, string>>();
        var page = Element("xPage", pageId, "", 0);
        page["x01"] = "5"; page["x02"] = "1"; page["x03"] = height.ToString(); page["x04"] = width.ToString();
        page["x05"] = "1"; page["x07"] = "sc:#FFFFFFFF"; Add("elements_normal", page);
        int rendered = 0;
        foreach (var section in xml.Root!.Elements().Where(e => e.Name.LocalName == "D"))
        foreach (var old in section.Elements())
        {
            string xaml = Value(old, "Content");
            if (string.IsNullOrWhiteSpace(xaml)) continue;
            try
            {
                double x = Number(old, "Left"), y = Number(old, "Top"), w = Number(old, "Width"), h = Number(old, "Height");
                if (w <= 0 || h <= 0) continue;
                int sequence = (int)Number(old, "zIndex");
                string text = Value(old, "Text");
                string function = Attribute(old, "Funktion");
                if (function == "Zeichnung" && TryNativeStandardShape(xaml, out var shapeType, out var shapeValues))
                {
                    var shape = Element(shapeType, pageId, "", sequence);
                    shape["name"] = "RRS Standardform";
                    shape["points"] = Points(x, y, w, h);
                    foreach (var (key, value) in shapeValues) shape[key] = value;
                    pageRows.Add(shape);
                    CountOne(shapeType == "xRectangle" ? "native_rectangles" : "native_ellipses");
                    rendered++;
                    continue;
                }
                if (function == "Zeichnung" && TryNativeLine(xaml, x, y, w, h, out var lineValues))
                {
                    var nativeLine = Element("xLine", pageId, "", sequence);
                    nativeLine["name"] = "RRS Linie";
                    foreach (var (key, value) in lineValues) nativeLine[key] = value;
                    pageRows.Add(nativeLine);
                    CountOne("native_lines");
                    rendered++;
                    continue;
                }
                if (function == "Zeichnung" && TryNativePolyline(xaml, x, y, w, h, out var polylineValues))
                {
                    var nativePolyline = Element("xPolyline", pageId, "", sequence);
                    nativePolyline["name"] = "RRS Polylinie";
                    foreach (var (key, value) in polylineValues) nativePolyline[key] = value;
                    pageRows.Add(nativePolyline);
                    CountOne("native_polylines");
                    rendered++;
                    continue;
                }
                if (function == "Zeichnung" && TryNativePath(xaml, out var pathValues))
                {
                    var nativePath = Element("xPathFixed", pageId, "", sequence);
                    nativePath["name"] = "RRS Zeichnung";
                    nativePath["points"] = Points(x, y, w, h);
                    foreach (var (key, value) in pathValues) nativePath[key] = value;
                    pageRows.Add(nativePath);
                    CountOne("native_paths");
                    rendered++;
                    continue;
                }
                if (function == "Text" && TryNativeStandardShape(xaml, out var textShapeType, out var textShapeValues))
                {
                    var outline = Element(textShapeType, pageId, "", sequence);
                    outline["name"] = "RRS Textfläche";
                    outline["points"] = Points(x, y, w, h);
                    foreach (var (key, value) in textShapeValues) outline[key] = value;
                    pageRows.Add(outline);
                    CountOne("native_text_rectangles");
                }
                else if (function == "Text" && TryNativePath(xaml, out var textPath))
                {
                    var outline = Element("xPathFixed", pageId, "", sequence);
                    outline["name"] = "RRS Textfläche";
                    outline["points"] = Points(x, y, w, h);
                    foreach (var (key, value) in textPath) outline[key] = value;
                    pageRows.Add(outline);
                    CountOne("native_text_paths");
                }
                else if (function == "Betriebsmittel" && Guid.TryParse(Value(old, "ID"), out _))
                {
                    var component = Element("xComponent", pageId, ComponentId(ispId, oldPageId, Value(old, "ID")), sequence);
                    component["points"] = Points(x, y, w, h); component["x02"] = "1";
                    component["x03"] = "0"; component["x04"] = "1"; component["x05"] = "1";
                    // RRX paints component key and function only when a text orientation is assigned.
                    // RRS painted those labels dynamically, outside the stored symbol XAML.
                    component["x20"] = "Top_Center";
                    component["x23"] = $"{F(x)};{F(y)}";
                    if (docks.TryGetValue(Value(old, "ID"), out var dock))
                    {
                        component["x21"] = "1";
                        component["x22"] = dock.Orientation;
                        component["x23"] = $"{F(dock.X)};{F(dock.Y)}";
                        CountOne("component_docks");
                    }
                    XElement? kamm = old.Element("Kamm");
                    double infoX = x, infoY = y;
                    if (kamm != null && double.TryParse(Attribute(kamm, "X"), NumberStyles.Float, CultureInfo.InvariantCulture, out double offsetInfoX)
                        && double.TryParse(Attribute(kamm, "Y"), NumberStyles.Float, CultureInfo.InvariantCulture, out double offsetInfoY))
                    {
                        infoX += offsetInfoX; infoY += offsetInfoY;
                    }
                    component["x24"] = "1"; component["x25"] = "0";
                    component["x26"] = $"{F(infoX)};{F(infoY)}";
                    if (TryNativePath(xaml, out var componentPath))
                    {
                        component["combined"] = CombinedPath(componentPath, w, h);
                        CountOne("native_component_symbols");
                    }
                    else if (WpfSvgConverter.TryConvert(xaml, w, h, out var componentSvg, out var componentAngle))
                    {
                        component["combined"] = CombinedSvg(componentSvg, w, h, componentAngle);
                        CountOne("svg_component_symbols");
                    }
                    else
                    {
                        component["combined"] = CombinedImage(RenderVisual(xaml, w, h), VectorOrRaster(xaml, w, h), w, h);
                        CountOne("compatibility_bitmaps");
                        if (IsVectorDrawing(xaml)) CountOne("vector_component_symbols");
                        else CountOne("image_component_symbols");
                    }
                    pageRows.Add(component);
                    CountOne("linked_components");
                }
                else if (WpfSvgConverter.TryConvert(xaml, w, h, out var svg, out var svgAngle))
                {
                    var vector = Element("xSvg", pageId, "", sequence);
                    vector["name"] = function;
                    vector["points"] = Points(x, y, w, h);
                    vector["x02"] = "1"; vector["x03"] = F(svgAngle);
                    vector["x04"] = "1"; vector["x05"] = "1";
                    vector["x06"] = svg; vector["x07"] = "1";
                    pageRows.Add(vector);
                    CountOne("svg_symbols");
                }
                else
                {
                    string png = RenderVisual(xaml, w, h);
                    string vector = VectorOrRaster(xaml, w, h);
                    CountOne("compatibility_bitmaps");
                    var image = Element("xImage", pageId, "", sequence);
                    image["name"] = function; image["points"] = Points(x, y, w, h);
                    image["x02"] = "1"; image["x03"] = "0"; image["x04"] = "1"; image["x05"] = "1";
                    image["x06"] = png; image["x07"] = "1"; image["x08"] = "0";
                    image["x09"] = vector; pageRows.Add(image);
                }
                if (function == "Text" && !string.IsNullOrWhiteSpace(text))
                {
                    XElement? oldText = old.Element("Text");
                    string oldSize = oldText == null ? "" : Attribute(oldText, "Schriftgröße");
                    string oldWeight = oldText == null ? "" : Attribute(oldText, "Größe");
                    string oldAlignment = oldText == null ? "" : Attribute(oldText, "Alignment");
                    string oldVertical = oldText == null ? "" : Attribute(oldText, "Vertikal");
                    string oldColor = oldText == null ? "" : Attribute(oldText, "Farbe");
                    string oldFont = oldText == null ? "" : Attribute(oldText, "Schriftart");
                    var label = Element("xTextbox", pageId, "", sequence);
                    label["name"] = "RRS Text"; label["points"] = Points(x, y, w, h);
                    label["x02"] = "1"; label["x03"] = "0"; label["x04"] = "0"; label["x05"] = "0";
                    label["x06"] = "sc:#00000000"; label["x07"] = "sc:#00FFFFFF"; label["x08"] = "0"; label["x09"] = "0";
                    label["x10"] = "0"; label["x11"] = "1"; label["x12"] = "1"; label["x13"] = text;
                    label["x14"] = double.TryParse(oldSize, NumberStyles.Float, CultureInfo.InvariantCulture, out double fontSize) ? F(fontSize) : F(Math.Clamp(h * .35, 7, 36));
                    label["x15"] = oldText == null || Attribute(oldText, "Style") != "Italic" ? "0" : "1";
                    label["x16"] = oldWeight switch { "Bold" => "1", "Light" => "8", _ => "0" };
                    label["x17"] = "0";
                    label["x18"] = "sc:" + (string.IsNullOrEmpty(oldColor) ? "#FF000000" : oldColor);
                    label["x19"] = string.IsNullOrEmpty(oldFont) ? "Arial" : oldFont;
                    label["x20"] = oldAlignment switch { "Right" => "1", "Center" => "2", _ => "0" };
                    label["x21"] = oldVertical switch { "Center" => "1", "Bottom" => "2", _ => "0" };
                    label["x22"] = "0";
                    label["x23"] = oldText != null && Attribute(oldText, "Warp") == "Wrap" ? "1" : "0";
                    label["x24"] = "Text"; pageRows.Add(label);
                }
                rendered++;
            }
            catch (Exception ex) { Warn($"XAML failed {file} item {Value(old, "ID")}: {ex.Message}"); }
        }
        foreach (var route in RrsConnections.Read(xml))
        {
            var line = Element("xPolyline", pageId, "", route.Sequence);
            line["name"] = "RRS Verbindung";
            line["points"] = string.Join("|", route.Points.Select(p => $"{F(p.X)};{F(p.Y)}"));
            line["x01"] = "";
            line["x02"] = "1";
            line["x03"] = F(route.Thickness);
            line["x04"] = "sc:" + route.Color;
            line["x05"] = route.Dash;
            line["x06"] = ((int)PenLineCap.Round).ToString(CultureInfo.InvariantCulture);
            pageRows.Add(line);
            CountOne("connection_lines");
        }
        foreach (var row in pageRows.OrderBy(row => int.Parse(row["sequence"], CultureInfo.InvariantCulture)))
            Add("elements_normal", row);
        CountOne("pages");
        Console.WriteLine($"  {pageId}: {rendered} independently placed elements");
    }

    static Dictionary<string, (double X, double Y, string Orientation)> ReadComponentDocks(XDocument xml)
    {
        var docks = new Dictionary<string, (double X, double Y, string Orientation)>(StringComparer.OrdinalIgnoreCase);
        var components = xml.Root?.Element("D")?.Elements("Di")
            .Where(e => Attribute(e, "Funktion") == "Betriebsmittel")
            .Select(e => Value(e, "ID"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anchors = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in xml.Root?.Element("DaP")?.Elements("DaPi") ?? Enumerable.Empty<XElement>())
        {
            string id = Value(anchor, "ID");
            if (id.Length > 0) anchors.TryAdd(id, anchor);
        }

        foreach (var link in xml.Root?.Element("Connections")?.Elements("Connection") ?? Enumerable.Empty<XElement>())
        {
            string sourceId = Value(link, "SourceID");
            if (!components.Contains(sourceId)) continue;
            if (!anchors.TryGetValue(Value(link, "SinkID"), out var anchor))
            {
                Warn($"Missing RRS dock anchor for component {sourceId}");
                continue;
            }
            string? orientation = Value(link, "SourceConnectorName") switch
            {
                "Top" => "1", "Left" => "2", "Right" => "3", "Bottom" => "4", _ => null
            };
            if (orientation == null)
            {
                Warn($"Unsupported RRS dock orientation for component {sourceId}");
                continue;
            }
            double dockX = Number(anchor, "Left") + Number(anchor, "Width") / 2;
            double dockY = Number(anchor, "Top") + Number(anchor, "Height") / 2;
            if (!docks.TryAdd(sourceId, (dockX, dockY, orientation)))
                Warn($"Multiple RRS dock connections for component {sourceId}");
        }
        return docks;
    }

    static void Data(SqliteConnection old, string ispId)
    {
        foreach (var r in Read(old, "Betriebsmittel"))
        {
            string oldPageId = Get(r, "ExportID");
            var v = DataRow(PageId(ispId, oldPageId), ComponentId(ispId, oldPageId, Get(r, "GUID")), ""); v["x00"] = "0";
            v["x03"] = Get(r, "Vorzeichen"); v["x04"] = Get(r, "Zähler"); v["x06"] = Get(r, "Funktionstext");
            v["x07"] = Get(r, "Montageort"); v["x08"] = Get(r, "TechnischeAngabe"); v["x12"] = Get(r, "Graviertext");
            v["x16"] = Get(r, "Anlage"); v["x17"] = Get(r, "AnlagenBeschreibung");
            v["x73"] = "1"; // RRX's normal component factor; blank becomes zero and paints "x0".
            Add("components", v);
        }
        int pointIndex = 0;
        foreach (var r in Read(old, "Datenpunkt"))
        {
            string oldPageId = Get(r, "ExportID");
            var v = DataRow(PageId(ispId, oldPageId), StableId($"datapoint|{ispId}|{oldPageId}|{Get(r, "RelationID")}|{pointIndex++}"), ComponentId(ispId, oldPageId, Get(r, "GUID")));
            v["x02"] = Get(r, "Vorzeichen"); v["x03"] = Get(r, "Zähler"); v["x05"] = Get(r, "Art");
            v["x06"] = Get(r, "Kommunikation"); v["x07"] = Get(r, "Signal"); v["x11"] = Get(r, "Sonstiges");
            v["x12"] = Get(r, "Funktion"); v["x13"] = Get(r, "FunktionKurz");
            // RRX x17 expects one named VDI function-list entry. The RRS VDI_A_1..80
            // flag array is a different representation; function lists are omitted.
            v["x20"] = Get(r, "ZF_1"); v["x21"] = Get(r, "ZF_2"); v["x31"] = Get(r, "Bemerkung");
            v["x32"] = Get(r, "Typ"); Add("datapoints", v);
        }
        int index = 0;
        foreach (var r in Read(old, "Kabel"))
        {
            string oldPageId = Get(r, "ExportID");
            var v = DataRow(PageId(ispId, oldPageId), StableId($"wire|{ispId}|{oldPageId}|{Get(r, "GUID")}|{index++}"), ComponentId(ispId, oldPageId, Get(r, "GUID")));
            v["x00"] = Get(r, "Vorzeichen"); v["x01"] = Get(r, "Zähler"); v["x02"] = Get(r, "Funktion");
            v["x04"] = Get(r, "Typ"); v["x05"] = Get(r, "Querschnitt"); v["x06"] = Get(r, "Startpunkt");
            v["x10"] = Get(r, "Verbraucher") == "True" ? "1" : ""; v["x15"] = Get(r, "Spannung");
            v["x16"] = NumberString(Get(r, "Strom")); v["x17"] = NumberString(Get(r, "Leistung"));
            v["x20"] = NumberString(Get(r, "Länge")); v["x22"] = Get(r, "Bemerkung"); v["x30"] = Get(r, "Sonstiges");
            Add("wires", v);
        }
        foreach (string table in new[] { "Artikel", "Zusatzartikel" })
        {
            index = 0;
            foreach (var r in Read(old, table))
            {
                string oldPageId = Get(r, "ExportID");
                var v = DataRow(PageId(ispId, oldPageId), StableId($"{table}|{ispId}|{oldPageId}|{Get(r, "GUID")}|{index++}"), ComponentId(ispId, oldPageId, Get(r, "GUID")));
                v["x02"] = Get(r, "Bezeichnung"); v["x03"] = Get(r, "Beschreibung"); v["x04"] = Get(r, "Hersteller");
                v["x05"] = Get(r, "HerstellerKurz"); v["x06"] = Get(r, "Lieferant"); v["x07"] = Get(r, "Produktgruppe");
                v["x08"] = Get(r, "Bauseits") == "True" ? "1" : ""; v["x09"] = Get(r, "EAN");
                v["x10"] = Get(r, "Bestellnummer"); v["x11"] = NumberString(Get(r, "Menge")); v["x12"] = Get(r, "Einheit");
                v["x18"] = Get(r, "Bereich"); v["x19"] = Get(r, "Positionsart"); v["x20"] = Get(r, "LvKurz");
                v["x21"] = Get(r, "LvLang");
                for (int i = 1; i <= 4; i++) { v[$"x{25 + i * 2:00}"] = Get(r, $"Datei{i}Funktion"); v[$"x{26 + i * 2:00}"] = Get(r, $"Datei{i}Pfad"); }
                for (int i = 1; i <= 15; i++) v[$"x{39 + i:00}"] = Get(r, $"FX{i}");
                v["x60"] = Get(r, "Bemerkung"); v["x61"] = Get(r, "Sonstiges");
                Add(table == "Artikel" ? "articles" : "additionalarticles", v);
            }
        }
    }

    static IEnumerable<Dictionary<string, string>> Read(SqliteConnection db, string table)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = $"SELECT * FROM \"{table}\"";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString() ?? "";
            yield return row;
        }
    }
    static string IspName(SqliteConnection db, string id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Graviertext FROM Betriebsmittel WHERE Funktionstext LIKE '%Schaltschrank%' AND Graviertext <> '' LIMIT 1";
        string? s = cmd.ExecuteScalar()?.ToString(); return string.IsNullOrWhiteSpace(s) ? "ISP " + id[..8] : s.Trim();
    }
    static void ApplyHeader(Dictionary<string, string> row, XElement header)
    {
        var mapping = new Dictionary<string, string>
        {
            ["x30"] = "Firma", ["x31"] = "Kunde", ["x32"] = "Projekt",
            ["x33"] = "Kommission", ["x34"] = "Zeichnungsnummer", ["x35"] = "Hinweis",
            ["x36"] = "Ersteller", ["x37"] = "ErstellerDatum",
            ["x38"] = "Geprüft", ["x39"] = "GeprüftDatum",
            ["x40"] = "Änderung1", ["x41"] = "ÄnderungDatum1", ["x42"] = "ÄnderungName1",
            ["x43"] = "Änderung2", ["x44"] = "ÄnderungDatum2", ["x45"] = "ÄnderungName2",
            ["x46"] = "Änderung3", ["x47"] = "ÄnderungDatum3", ["x48"] = "ÄnderungName3",
        };
        foreach (var (column, attribute) in mapping) row[column] = Attribute(header, attribute);
    }
    static string RenderVisual(string xaml, double width, double height)
    {
        string key = $"{F(width)}|{F(height)}|{xaml}";
        if (ImageCache.TryGetValue(key, out string? cached)) return cached;
        if (XamlReader.Parse(xaml) is not UIElement visual) throw new InvalidDataException("XAML is not a UIElement");
        int pixelWidth = (int)Math.Ceiling(width), pixelHeight = (int)Math.Ceiling(height);
        if (pixelWidth > 4000 || pixelHeight > 4000) throw new InvalidDataException("Drawing element too large");
        var canvas = new Grid { Width = pixelWidth, Height = pixelHeight, Background = Brushes.Transparent };
        canvas.Children.Add(new ContentPresenter
        {
            Content = visual,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            SnapsToDevicePixels = true,
            UseLayoutRounding = false
        });
        canvas.Measure(new Size(pixelWidth, pixelHeight));
        canvas.Arrange(new Rect(0, 0, pixelWidth, pixelHeight)); canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output);
        string result = Convert.ToBase64String(output.ToArray());
        if (ImageCache.Count < 10000) ImageCache[key] = result;
        return result;
    }
    static bool IsVectorDrawing(string xaml) => xaml.Contains("<DrawingImage", StringComparison.Ordinal)
        || xaml.TrimStart().StartsWith("<Path", StringComparison.Ordinal)
        || xaml.TrimStart().StartsWith("<Rectangle", StringComparison.Ordinal)
        || xaml.TrimStart().StartsWith("<Viewbox", StringComparison.Ordinal)
        || xaml.TrimStart().StartsWith("<Canvas", StringComparison.Ordinal);
    static string VectorOrRaster(string xaml, double width, double height)
    {
        if (IsVectorDrawing(xaml))
        {
            CountOne("vector_symbols");
            return "rrs-xaml:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(xaml));
        }
        CountOne("raster_symbols");
        return RenderVisual(xaml, width, height);
    }
    static bool TryNativeStandardShape(string xaml, out string type, out Dictionary<string, string> values)
    {
        type = "";
        values = new Dictionary<string, string>();
        try
        {
            if (XamlReader.Parse(xaml) is not System.Windows.Shapes.Path path || path.Data is null)
                return false;
            double rotation = path.LayoutTransform switch
            {
                null => 0,
                RotateTransform rotate => rotate.Angle,
                Transform transform when transform.Value.IsIdentity => 0,
                _ => double.NaN
            };
            if (!double.IsFinite(rotation) || Math.Abs(rotation / 90 - Math.Round(rotation / 90)) > 0.001)
                return false;
            if (path.RenderTransform is Transform render && !render.Value.IsIdentity) return false;
            if (path.Stroke is not SolidColorBrush stroke || path.Fill is not SolidColorBrush fill)
                return false;
            string dash = path.StrokeDashArray?.ToString(CultureInfo.InvariantCulture) ?? "0";
            string cap = ((int)path.StrokeStartLineCap).ToString(CultureInfo.InvariantCulture);
            if (path.Data is RectangleGeometry rectangle)
            {
                type = "xRectangle";
                values["x01"] = "";
                values["x02"] = F(path.Opacity);
                values["x03"] = F(path.StrokeThickness);
                bool quarterTurn = ((int)Math.Round(Math.Abs(rotation) / 90)) % 2 != 0;
                values["x04"] = F(quarterTurn ? rectangle.RadiusY : rectangle.RadiusX);
                values["x05"] = F(quarterTurn ? rectangle.RadiusX : rectangle.RadiusY);
                values["x06"] = "sc:" + stroke.Color;
                values["x07"] = "sc:" + fill.Color;
                values["x08"] = dash;
                values["x09"] = cap;
                return true;
            }
            if (path.Data is EllipseGeometry)
            {
                type = "xEllipse";
                values["x01"] = "";
                values["x02"] = F(path.Opacity);
                values["x03"] = F(path.StrokeThickness);
                values["x04"] = "sc:" + stroke.Color;
                values["x05"] = "sc:" + fill.Color;
                values["x06"] = dash;
                values["x07"] = cap;
                return true;
            }
            return false;
        }
        catch { return false; }
    }
    static bool TryNativePath(string xaml, out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>();
        try
        {
            if (XamlReader.Parse(xaml) is not System.Windows.Shapes.Path path || path.Data == null
                || path.Stroke is not SolidColorBrush stroke || path.Fill is not SolidColorBrush fill)
                return false;
            // xPathFixed stretches its geometry in both axes. Zero-height or
            // zero-width RRS lines would become blocks or disappear in RRX.
            if (path.Data.Bounds.Width < 0.001 || path.Data.Bounds.Height < 0.001) return false;
            if (path.LayoutTransform != null && path.LayoutTransform is not (RotateTransform or MatrixTransform)) return false;
            if (path.LayoutTransform is MatrixTransform matrix && !matrix.Matrix.IsIdentity) return false;
            // RRX applies rotation after its own path stretch and moves the result.
            // RRS LayoutTransform is measured before the surrounding item is sized.
            // Keep rotated paths as images until the geometry can be transformed exactly.
            if (path.LayoutTransform is RotateTransform rotated && Math.Abs(rotated.Angle) > 0.001) return false;
            // RectangleGeometry, EllipseGeometry and LineGeometry do not serialize
            // to the mini-language that RRX Geometry.Parse expects. Normalize first.
            string data = PathGeometry.CreateFromGeometry(path.Data).ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(data)) return false;
            Geometry.Parse(data);
            values["x01"] = "";
            values["x02"] = F(path.Opacity);
            values["x03"] = F(path.StrokeThickness);
            values["x04"] = "sc:" + stroke.Color;
            values["x05"] = "sc:" + fill.Color;
            values["x06"] = path.StrokeDashArray?.ToString(CultureInfo.InvariantCulture) ?? "0";
            values["x07"] = ((int)path.StrokeStartLineCap).ToString(CultureInfo.InvariantCulture);
            values["x08"] = data;
            values["x09"] = path.LayoutTransform is RotateTransform rotate ? F(rotate.Angle) : "0";
            values["x10"] = "1";
            values["x11"] = "1";
            return true;
        }
        catch { return false; }
    }
    static bool TryNativePolyline(string xaml, double x, double y, double w, double h, out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>();
        try
        {
            if (XamlReader.Parse(xaml) is not System.Windows.Shapes.Path path
                || path.Data is not PathGeometry geometry || geometry.Figures.Count != 1
                || geometry.Figures[0].IsClosed || path.Stroke is not SolidColorBrush stroke
                || path.Fill is not SolidColorBrush fill || fill.Color.A != 0)
                return false;
            double angle = path.LayoutTransform switch
            {
                null => 0,
                RotateTransform rotate => rotate.Angle,
                Transform layout when layout.Value.IsIdentity => 0,
                _ => double.NaN
            };
            if (!double.IsFinite(angle) || Math.Abs(angle / 90 - Math.Round(angle / 90)) > 0.001) return false;
            if (path.RenderTransform is Transform render && !render.Value.IsIdentity) return false;
            var sourcePoints = new List<Point> { geometry.Figures[0].StartPoint };
            foreach (PathSegment segment in geometry.Figures[0].Segments)
            {
                if (segment is LineSegment line) sourcePoints.Add(line.Point);
                else if (segment is PolyLineSegment poly) sourcePoints.AddRange(poly.Points);
                else return false;
            }
            if (sourcePoints.Count < 2) return false;
            if (Math.Abs(angle) > 0.001)
            {
                Rect original = geometry.Bounds;
                double cx = original.Left + original.Width / 2;
                double cy = original.Top + original.Height / 2;
                double radians = angle * Math.PI / 180;
                double cos = Math.Cos(radians), sin = Math.Sin(radians);
                sourcePoints = sourcePoints.Select(p => new Point(
                    cx + (p.X - cx) * cos - (p.Y - cy) * sin,
                    cy + (p.X - cx) * sin + (p.Y - cy) * cos)).ToList();
            }
            Rect bounds = new Rect(
                new Point(sourcePoints.Min(p => p.X), sourcePoints.Min(p => p.Y)),
                new Point(sourcePoints.Max(p => p.X), sourcePoints.Max(p => p.Y)));
            if (bounds.Width < 0.001 || bounds.Height < 0.001) return false;
            double insetX = Math.Min(path.StrokeThickness / 2, w / 2);
            double insetY = Math.Min(path.StrokeThickness / 2, h / 2);
            var mapped = sourcePoints.Select(p => new Point(
                x + insetX + (p.X - bounds.Left) / bounds.Width * (w - 2 * insetX),
                y + insetY + (p.Y - bounds.Top) / bounds.Height * (h - 2 * insetY)));
            values["points"] = string.Join("|", mapped.Select(p => $"{F(p.X)};{F(p.Y)}"));
            values["x01"] = "";
            values["x02"] = F(path.Opacity);
            values["x03"] = F(path.StrokeThickness);
            values["x04"] = "sc:" + stroke.Color;
            values["x05"] = path.StrokeDashArray?.ToString(CultureInfo.InvariantCulture) ?? "0";
            values["x06"] = ((int)path.StrokeStartLineCap).ToString(CultureInfo.InvariantCulture);
            return true;
        }
        catch { return false; }
    }
    static bool TryNativeLine(string xaml, double x, double y, double w, double h, out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>();
        try
        {
            if (XamlReader.Parse(xaml) is not System.Windows.Shapes.Path path || path.Data is not LineGeometry line
                || path.Stroke is not SolidColorBrush stroke) return false;
            double rotation = path.LayoutTransform switch
            {
                null => 0,
                RotateTransform r => r.Angle,
                MatrixTransform m when m.Matrix.IsIdentity => 0,
                _ => double.NaN
            };
            if (!double.IsFinite(rotation) || Math.Abs(rotation / 90 - Math.Round(rotation / 90)) > 0.001) return false;
            bool originalHorizontal = Math.Abs(line.EndPoint.X - line.StartPoint.X) >= Math.Abs(line.EndPoint.Y - line.StartPoint.Y);
            bool rotatedQuarter = ((int)Math.Round(Math.Abs(rotation) / 90)) % 2 == 1;
            bool horizontal = originalHorizontal != rotatedQuarter;
            double inset = Math.Min(path.StrokeThickness / 2, horizontal ? w / 2 : h / 2);
            double x1 = horizontal ? x + inset : x + w / 2;
            double y1 = horizontal ? y + h / 2 : y + inset;
            double x2 = horizontal ? x + w - inset : x + w / 2;
            double y2 = horizontal ? y + h / 2 : y + h - inset;
            values["points"] = $"{F(x1)};{F(y1)}|{F(x2)};{F(y2)}";
            values["x01"] = "";
            values["x02"] = F(path.Opacity);
            values["x03"] = F(path.StrokeThickness);
            values["x04"] = "sc:" + stroke.Color;
            values["x05"] = path.StrokeDashArray?.ToString(CultureInfo.InvariantCulture) ?? "0";
            values["x06"] = ((int)path.StrokeStartLineCap).ToString(CultureInfo.InvariantCulture);
            values["x07"] = "0";
            return true;
        }
        catch { return false; }
    }
    static string CombinedPath(Dictionary<string, string> path, double w, double h)
    {
        var child = new XElement("x.ptf",
            new XAttribute("nm", "RRS Symbolpfad"), new XAttribute("pts", Points(0, 0, w, h)),
            new XAttribute("lck", path["x01"]), new XAttribute("opt", path["x02"]),
            new XAttribute("thk", path["x03"]), new XAttribute("str", path["x04"]),
            new XAttribute("fil", path["x05"]), new XAttribute("dsh", path["x06"]),
            new XAttribute("cap", path["x07"]), new XAttribute("dat", path["x08"]),
            new XAttribute("swp", path["x09"]), new XAttribute("sx", path["x10"]), new XAttribute("sy", path["x11"]));
        var xml = new XElement("x.sht", new XAttribute("dim", $"{F(w)};{F(h)}"), child);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));
        return Convert.ToBase64String(ms.ToArray());
    }
    static string CombinedImage(string png, string vector, double w, double h)
    {
        var img = new XElement("x.img", new XAttribute("nm", "RRS Auswahlfläche"), new XAttribute("pts", Points(0, 0, w, h)),
            new XAttribute("lck", ""), new XAttribute("opt", "1"), new XAttribute("swp", "0"), new XAttribute("sx", "1"),
            new XAttribute("sy", "1"), new XAttribute("img", png), new XAttribute("str", "1"), new XAttribute("lnk", "0"),
            new XAttribute("vct", vector));
        var xml = new XElement("x.sht", new XAttribute("dim", $"{F(w)};{F(h)}"), img);
        using var ms = new MemoryStream(); using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));
        return Convert.ToBase64String(ms.ToArray());
    }
    static string CombinedSvg(string svg, double w, double h, double angle)
    {
        var child = new XElement("x.svg", new XAttribute("nm", "RRS Vektorsymbol"),
            new XAttribute("pts", Points(0, 0, w, h)), new XAttribute("opt", "1"),
            new XAttribute("swp", F(angle)), new XAttribute("sx", "1"), new XAttribute("sy", "1"),
            new XAttribute("svg", svg), new XAttribute("str", "1"));
        var xml = new XElement("x.sht", new XAttribute("dim", $"{F(w)};{F(h)}"), child);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
            gz.Write(Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));
        return Convert.ToBase64String(ms.ToArray());
    }
    static Dictionary<string, string> Item(string type, string pid, string uid, string name, string description)
        => new() { ["type"] = type, ["pid"] = pid, ["uid"] = uid, ["name"] = name, ["description"] = description };
    static Dictionary<string, string> Element(string type, string pid, string uid, int sequence)
        => new() { ["type"] = type, ["pid"] = pid, ["uid"] = uid, ["cid"] = "", ["gid"] = "", ["name"] = "",
            ["points"] = "", ["sequence"] = sequence.ToString(CultureInfo.InvariantCulture), ["combined"] = "", ["pins"] = "", ["ports"] = "" };
    static Dictionary<string, string> DataRow(string pid, string uid, string cid) => new() { ["type"] = "", ["pid"] = pid, ["uid"] = uid, ["cid"] = cid };
    static void Add(string table, Dictionary<string, string> row) { if (!writers.TryGetValue(table, out var writer)) writers[table] = writer = new Writer(table); writer.Add(row); CountOne(table); }
    static void Exec(string sql) { using var cmd = target.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    static void CountOne(string key) => Count[key] = Count.GetValueOrDefault(key) + 1;
    static void Warn(string s) { Warnings.Add(s); if (Warnings.Count <= 10) Console.Error.WriteLine(s); }
    static string Attribute(XElement e, string k) => (string?)e.Attribute(k) ?? "";
    static string Value(XElement e, string k) => (string?)e.Element(k) ?? "";
    static string Get(Dictionary<string, string> r, string k) => r.GetValueOrDefault(k) ?? "";
    static double Number(XElement e, string k) => double.TryParse(Value(e, k), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
    static string F(double n) => n.ToString(CultureInfo.InvariantCulture);
    static string Points(double x, double y, double w, double h) => $"{F(x)};{F(y)}|{F(x + w)};{F(y + h)}";
    static string NumberString(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.GetCultureInfo("de-DE"), out var n) ? F(n) : "0";
    static string StableId(string s) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToString();
    static string PageId(string ispId, string oldPageId) => StableId($"page|{ispId}|{oldPageId}");
    static string ComponentId(string ispId, string oldPageId, string oldComponentId) => StableId($"component|{ispId}|{oldPageId}|{oldComponentId}");
    sealed class Writer : IDisposable
    {
        readonly string[] columns; readonly SqliteCommand cmd;
        public Writer(string table)
        {
            using var info = target.CreateCommand(); info.Transaction = tx; info.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var reader = info.ExecuteReader(); var names = new List<string>(); while (reader.Read()) names.Add(reader.GetString(1));
            columns = names.ToArray(); cmd = target.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO \"{table}\" ({string.Join(",", columns.Select(x => "\"" + x + "\""))}) VALUES ({string.Join(",", columns.Select((_, i) => "@p" + i))})";
            for (int i = 0; i < columns.Length; i++) cmd.Parameters.AddWithValue("@p" + i, ""); cmd.Prepare();
        }
        public void Add(Dictionary<string, string> row) { for (int i = 0; i < columns.Length; i++) cmd.Parameters[i].Value = row.GetValueOrDefault(columns[i]) ?? ""; cmd.ExecuteNonQuery(); }
        public void Dispose() => cmd.Dispose();
    }
}
