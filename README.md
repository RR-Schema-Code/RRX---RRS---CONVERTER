# RRS-Projekte nach RRX übernehmen

Dieses Werkzeug liest ein altes RRS-Projekt und erstellt daraus eine neue RRX-Projektdatei (`.rrxp`). Es benötigt keine Bedienung des RRX-Zeichenfensters während der Konvertierung. Die alten Dateien und das installierte RRX-Programm werden nicht verändert.

## Das brauchst du

1. Einen **Windows-PC** mit dem [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Auf der Downloadseite „SDK“ für Windows wählen. Nach der Installation den PC oder das Terminal neu starten.
2. Den **vollständigen Ordner** des alten RRS-Projekts. Direkt darin muss `Projekt.xml` liegen. Die Unterordner mit Zeichnungen und Datenbanken müssen mitkommen.
3. Eine **leere RRX-Projektdatei** (`.rrxp`) aus der RRX-Version, mit der du das Ergebnis später öffnen willst. Erstelle sie in deinem installierten RRX und speichere sie an einem eigenen Ort. Verwende kein Kundenprojekt als Vorlage.

Projektdateien und RRX-Programmdateien sind in diesem Repository nicht enthalten.

## Konvertierung per Doppelklick

1. Oben auf **Code → Download ZIP** klicken und das ZIP vollständig entpacken.
2. `Starten.cmd` im entpackten Ordner doppelt anklicken.
3. Im ersten Fenster den alten RRS-Projektordner mit `Projekt.xml` auswählen.
4. Im zweiten Fenster die leere RRX-`.rrxp` auswählen.
5. Im dritten Fenster Namen und Ort der **neuen** `.rrxp` wählen. Die neue Datei darf nicht die leere Vorlage sein und muss außerhalb des RRS-Projektordners liegen.
6. Warten, bis „Fertig“ erscheint. Die neue `.rrxp` mit dem installierten RRX öffnen.

Neben der Ausgabedatei entsteht eine Datei mit der Endung `.report.json`. Sie enthält Zählwerte und Warnungen. Wenn Warnungen erscheinen, die betroffenen Blätter und Daten in RRX prüfen. Der Bericht kann vollständige lokale Pfade enthalten; er gehört **nicht** auf GitHub.

## Was übernommen wird

- Projektstruktur, Ordner und Zeichnungsblätter.
- Bearbeitbare RRX-Grundformen wie Linien, Polylinien, Rechtecke und Ellipsen; Texte und Ebenenreihenfolge.
- Betriebsmittel, Datenpunkte, Kabel, Artikel und Zusatzartikel samt Seitenbezug.
- Betriebsmittel-Info-Punkte und Andockpunkte am Blattrand, soweit sie im alten Projekt vorhanden sind.
- Zusammengesetzte Symbole als Vektorgrafik, soweit möglich, sonst als Bild.

Deckblätter werden neu und leer angelegt. Von Inhaltsverzeichnissen wird nur die Ordnerstruktur übernommen. Funktionslisten, Rasterlinien und Normblätter werden ausgelassen.

**Wichtig:** Die automatische Übernahme ersetzt keine fachliche Sichtprüfung. Vergleiche nach dem Öffnen in RRX besonders Zeichnungen, Leitungsführung, Texte, Betriebsmittel, Datenpunkte, Kabel und Artikel mit dem alten Projekt. Bei fehlenden alten Dateien kann die Ausgabe unvollständig sein. Die Leitungsführung zwischen RRS-XPoints nutzt in dieser öffentlichen Version einen eigenständigen orthogonalen Router; ihr Verlauf kann deshalb vom RRS-Bild abweichen.

## Für Fortgeschrittene

Der direkte Aufruf mit drei Pfaden lautet:

```powershell
dotnet run --project src/RrsToRrx.csproj -- "C:\Pfad\Altes-RRS-Projekt" "C:\Pfad\Leere-Vorlage.rrxp" "C:\Pfad\Neues-Projekt.rrxp"
```

Mit Python 3 lässt sich das Ergebnis zusätzlich auf Datenbezüge prüfen:

```powershell
python tools/validate.py "C:\Pfad\Altes-RRS-Projekt" "C:\Pfad\Neues-Projekt.rrxp"
```

Die [technische Beschreibung](docs/TECHNIK.md) erklärt die Zuordnung. [Hinweise für eine andere KI](docs/FUER-KI.md) beschreiben Aufbau und Prüfregeln.

Für dieses Repository ist derzeit **keine Lizenzdatei** hinterlegt.
