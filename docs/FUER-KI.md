# Hinweise für eine andere KI

Aufgabe: Den Konverter in diesem Repository pflegen oder einen Nutzer bei der Konvertierung eines lokal vorhandenen RRS-Projekts unterstützen.

## Grenzen und Eingaben

- Die Quellprojekte bleiben lokal. Projektdateien, Kundennamen, Ausgaben, Screenshots, Berichte und leere RRX-Vorlagen nicht in dieses öffentliche Repository einchecken.
- Das installierte RRX-Programm nicht ändern. Die `.rrxp`-Vorlage kommt aus der installierten Zielversion und wird nur als Eingabe benutzt.
- Windows mit .NET 10 SDK ist erforderlich. `src/RrsToRrx.csproj` baut ohne RRS-Quellcode. `src/OrthogonalRouter.cs` übernimmt die XPoint-Leitungsführung eigenständig.
- Die Befehle aus `README.md` akzeptieren genau drei Pfade: RRS-Projektordner, leere RRX-`.rrxp`, neue Ausgabe-`.rrxp`.

## Konvertierung nachvollziehen

1. `src/Program.cs` liest `Projekt.xml`, die ISP-Verzeichnisse, `Seiten.xml`, die Seiten-XML und `Datenbank.db3`.
2. Die Ausgabe wird aus einer Kopie der leeren RRX-Vorlage erstellt. RRX-Tabellen werden innerhalb einer SQLite-Transaktion neu befüllt.
3. Zeichnungselemente werden nach RRS-`Canvas.ZIndex` sortiert und in RRX-Reihenfolge geschrieben. Grundformen bleiben native RRX-Elemente. Komplexe WPF-Symbole gehen über `src/WpfSvgConverter.cs` in Vektoren oder Bilder über.
4. `Kamm` bestimmt den Info-Punkt des `xComponent`. `Connections` zu `DaP` bestimmen den Dockpunkt. XPoint-Verbindungen werden als gezeichnete RRX-Linien über `src/OrthogonalRouter.cs` geführt.
5. Fachdatensätze werden mit stabilen Seiten- und Betriebsmittelbezügen geschrieben. Die Zuordnung steht im Abschnitt `Data()` von `src/Program.cs`.

Mehr Details stehen in [TECHNIK.md](TECHNIK.md).

## Prüfen

- `dotnet build src/RrsToRrx.csproj` muss ohne proprietäre RRS-Dateien funktionieren.
- `python tools/validate.py <RRS-Ordner> <Ausgabe.rrxp>` prüft Zahlen, Seiten- und Betriebsmittelbezüge sowie Info-Punkte. Python 3 ist nur für diese zusätzliche Prüfung nötig.
- Die `.report.json` auf Warnungen prüfen. Fehlende alte Dateien oder inkompatible Eingaben können eine teilweise Ausgabe erzeugen.
- Zeichnungen und Fachdaten in der installierten RRX-Version visuell und fachlich mit der RRS-Quelle vergleichen. Besonders XPoint-Leitungswege, Dockpunkte und Symbole prüfen.
- Vor jeder Veröffentlichung `git status` und die tatsächlich vorgemerkten Dateien prüfen. `bin`, `obj`, `.rrxp`, `.db3`, XML-Projektdaten, Berichte und Bilder sind ausgeschlossen.

Keine Kundenbeispiele in öffentliche Tests oder Dokumentation übernehmen. Für reproduzierbare Tests synthetische Daten ohne Personen- oder Projektnamen erstellen.
