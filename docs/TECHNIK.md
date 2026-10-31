# Technischer Konvertierungsweg

Diese Beschreibung bezieht sich auf den Stand des Konverters in `src/`. Sie dokumentiert die **beobachtete** Zuordnung der beiden Projektformate und die bewusst ausgelassenen Daten.

## 1. Eingaben und Ausgabe

1. `Projekt.xml` enthält die Projekt- und ISP-Struktur. Die ISP-Verzeichnisse werden über ihre GUID zugeordnet.
2. Normalerweise beschreibt `Seiten.xml` in jedem ISP die Ordner und Blätter. Eine Zeichnungsseite liegt in einer XML-Datei mit ihrer alten Blatt-ID als Dateiname. Fehlt die Seitenliste, werden die vorhandenen Zeichnungsdateien außer der Standard-Deckblatt-ID in einem Ersatzordner mit neutralen Blattnamen angelegt; ursprüngliche Namen und Reihenfolge bleiben unbekannt.
3. `Datenbank.db3` enthält `Betriebsmittel`, `Datenpunkt`, `Kabel`, `Artikel` und `Zusatzartikel`. Fehlt sie in einem ISP, werden dessen Zeichnungen dennoch übernommen; die zugehörigen Fachdaten fehlen dann und die Datenprüfung meldet nicht verknüpfte Symbole.
4. Eine leere `.rrxp` liefert die Tabellenstruktur der Zielversion. Der Konverter kopiert sie in eine **separate** Ausgabedatei, leert dort die Projekttabellen und füllt sie in einer SQLite-Transaktion.
5. Nach dem Schreiben erfolgt `PRAGMA integrity_check`. Der Bericht neben der Ausgabedatei enthält die gezählten Elemente und Warnungen.

Die leere Vorlage ist eine Eingabe, keine Datei, die zusammen mit dem Konverter veröffentlicht werden muss.

## 2. Projektbaum

Der Konverter schreibt ein `xProject`, je ISP ein `xAsp` und die entsprechenden Ordner. RRS-Deckblätter (`BlattD`) werden als neue leere `xCover` angelegt. Alte Inhaltsseiten (`Blatt`) werden ausgelassen; ihre Ordner bleiben bestehen. Zeichnungsblätter (`BlattA`) werden zu `xSystemPage`. Funktionslisten werden ausgelassen.

Seiten- und Betriebsmittel-IDs werden aus den alten IDs und der ISP-ID stabil abgeleitet. Dadurch können Zeichnung und Datenbank dieselben Zielobjekte referenzieren. Die RRX-Tabellen `components`, `datapoints`, `wires`, `articles` und `additionalarticles` erhalten `pid` für die Seite und `cid` für das Betriebsmittel. Ohne gültiges `pid` erscheinen Datensätze in RRX nicht auf der Seite.

## 3. Zeichnung und Ebenen

Aus der alten Seiten-XML wird der Bereich `D` für die Zeichnung gelesen. `Connections` und `DaP` liefern zusätzlich die Andockpunkte am Blattrand. `NP` (Normblatt) und `NR` werden nicht gezeichnet. Die einzelnen `DaP`-Anker werden nicht als sichtbare Formen übertragen; ihre Koordinaten fließen in die RRX-Betriebsmittel ein. Das RRS-Blatt hat im Pilotformat 1488 × 1052 Koordinateneinheiten; eine RRX-Seite übernimmt diese Größe.

RRS verwendet `Canvas.ZIndex`, während RRX die Elemente in Datenbankreihenfolge zeichnet. Deshalb sammelt der Konverter zuerst alle Elemente einer Seite und schreibt sie stabil sortiert nach `zIndex`. Bei gleichem Wert bleibt die ursprüngliche Reihenfolge erhalten.

Die Darstellung folgt dieser Reihenfolge:

1. Eigenständige RRS-`Path`-Elemente mit Rechteck-, Ellipsen-, Linien- oder geeigneter Polygonzug-Geometrie werden `xRectangle`, `xEllipse`, `xLine` oder `xPolyline`. Textflächen erhalten ebenfalls native Rechtecke und eigene `xTextbox`-Elemente.
2. Geeignete übrige Pfade werden `xPathFixed`.
3. Betriebsmittel werden `xComponent`. Ihre Symbolgrafik wird als nativer Pfad, SVG oder Ersatzbild im kombinierten Inhalt abgelegt.
4. Zusammengesetzte WPF-`DrawingImage`-Grafiken werden, wenn möglich, in SVG-Geometrie umgesetzt (`xSvg`). Kann die Geometrie nicht zuverlässig umgesetzt werden, wird eine PNG-Darstellung gespeichert; das alte XAML bleibt als Zusatzinformation erhalten.
5. RRS-Verbindungen aus `XPoints` und `ConnectionsXPoint` werden als RRX-Polylinien geschrieben. `src/OrthogonalRouter.cs` bestimmt dafür einen orthogonalen Leitungsweg zwischen den Anschlussstellen. Er kann anders verlaufen als die ursprüngliche RRS-Leitung. Verbindungen aus `Connections` zu `DaP` sind dagegen **Betriebsmittel-Docks** und werden über `xComponent` übertragen.

SVG und PNG betreffen vor allem zusammengesetzte Symbole. Ein eigenständiges RRS-Rechteck bleibt ein bearbeitbares RRX-Rechteck.

## 4. Betriebsmittel-Info

In der RRS-Zeichnung besitzt jedes Betriebsmittel einen `Kamm`-Punkt mit `X` und `Y` **relativ** zur Position des Betriebsmittels. Die Zielkoordinate lautet:

```text
InfoX = Betriebsmittel.Left + Kamm.X
InfoY = Betriebsmittel.Top  + Kamm.Y
```

Der Konverter schreibt `componentInfoVisible = 1`, `InfoOrientation = 0` und `componentInfoPoint = "InfoX;InfoY"` in `xComponent`. Er schreibt **kein separates** `xComponentInfo`: Dieses Element erzeugt RRX aus dem Betriebsmittel. Die Datenprüfung vergleicht für jedes Betriebsmittel den Zielpunkt mit dem RRS-Ausgangspunkt.

Der **Dockpunkt ist ein anderer Punkt**. RRS speichert dafür eine Verbindung vom Betriebsmittel (`Connections/Connection/SourceID`) zu einem Randanker (`SinkID` in `DaP/DaPi`). Der Zielpunkt ist die Mitte des `DaPi`-Rechtecks: `Left + Width/2`, `Top + Height/2`. Die RRS-`SourceConnectorName`-Richtung wird auf RRX `DockOrientation` abgebildet. Der Konverter setzt `componentDockVisible`, `componentDockPointOrientation` und `componentDockPoint` im `xComponent`-Datensatz; RRX erzeugt daraus `xComponentDock`. Auf Seiten ohne RRS-Randverbindung bleibt der Dock ausgeblendet. Die globale RRX-Seitenanzeige „Dock“ kann zusätzlich ein- oder ausgeschaltet werden.

## 5. Fachdaten

Die fünf RRS-Tabellen werden jeweils in die gleichnamige fachliche RRX-Tabelle übertragen. Die Feldzuordnung steht in `src/Program.cs` in `Data()`. Sie enthält unter anderem Kennzeichen, Funktionstexte, Kabeltyp und -länge sowie Artikelnummer, Hersteller und Menge. Dezimalzahlen aus deutschen Eingaben werden in die von RRX erwartete Schreibweise umgerechnet.

Die RRS-VDI-Flagreihe ist nicht dieselbe Darstellung wie der einzelne RRX-VDI-Eintrag; die Funktionsliste ist ausdrücklich ausgenommen. Bei weiteren RRS-Projektvarianten muss jede Feldzuordnung gegen echte Beispieldaten geprüft werden.

## 6. Prüfweg

1. Konverter ausführen und Warnungen im `.report.json` ansehen.
2. `tools/validate.py` ausführen. Es prüft Anzahlen, `pid`/`cid`, ausgewählte Feldwerte und die Info-Punkte.
3. Zeichnungen mit der RRS-Referenz vergleichen. Die öffentliche Version enthält keinen RRX-Renderer.
4. Das Ergebnis in der **installierten** Zielversion von RRX öffnen und besonders Ebenen, Texte, XPoint-Leitungswege, Datenpunkte, Kabel und Artikel prüfen.
