$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'Das .NET 10 SDK fehlt. Installiere es von https://dotnet.microsoft.com/download/dotnet/10.0' -ForegroundColor Red
    exit 1
}
$sdkList = & dotnet --list-sdks
if (-not ($sdkList | Where-Object { $_ -match '^10\.' })) {
    Write-Host 'Das .NET 10 SDK fehlt. Installiere es von https://dotnet.microsoft.com/download/dotnet/10.0' -ForegroundColor Red
    exit 1
}

Write-Host '1. Alten RRS-Projektordner auswählen (er enthält Projekt.xml).'
$folder = New-Object System.Windows.Forms.FolderBrowserDialog
$folder.Description = 'RRS-Projektordner mit Projekt.xml auswählen'
if ($folder.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit 0 }
$source = $folder.SelectedPath
if (-not (Test-Path -LiteralPath (Join-Path $source 'Projekt.xml'))) {
    Write-Host 'In diesem Ordner fehlt Projekt.xml.' -ForegroundColor Red
    exit 1
}

Write-Host '2. Leere RRX-Projektdatei als Vorlage auswählen.'
$templateDialog = New-Object System.Windows.Forms.OpenFileDialog
$templateDialog.Filter = 'RRX-Projekt (*.rrxp)|*.rrxp'
$templateDialog.Title = 'Leere RRX-Projektdatei auswählen'
if ($templateDialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit 0 }
$template = $templateDialog.FileName

Write-Host '3. Speicherort für das neue RRX-Projekt auswählen.'
$outputDialog = New-Object System.Windows.Forms.SaveFileDialog
$outputDialog.Filter = 'RRX-Projekt (*.rrxp)|*.rrxp'
$outputDialog.DefaultExt = 'rrxp'
$outputDialog.AddExtension = $true
$outputDialog.OverwritePrompt = $true
$outputDialog.Title = 'Neues RRX-Projekt speichern'
if ($outputDialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit 0 }
$output = $outputDialog.FileName
if ([string]::Equals($output, $template, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Host 'Die Ausgabedatei darf nicht die leere Vorlage überschreiben.' -ForegroundColor Red
    exit 1
}

$project = Join-Path $PSScriptRoot 'src\RrsToRrx.csproj'
Write-Host 'Konvertierung läuft ...'
& dotnet run --project $project -- $source $template $output
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Konvertierung fehlgeschlagen. Die Fehlermeldung steht oben.' -ForegroundColor Red
    exit $LASTEXITCODE
}

$report = [System.IO.Path]::ChangeExtension($output, '.report.json')
Write-Host "Fertig: $output" -ForegroundColor Green
Write-Host "Prüfbericht: $report"
if (Test-Path -LiteralPath $report) {
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    $warningCount = @($result.warnings | Where-Object { $null -ne $_ }).Count
    Write-Host "Warnungen: $warningCount"
    if ($warningCount -gt 0) { Write-Host 'Bitte den Bericht lesen und die betroffenen Blätter in RRX prüfen.' -ForegroundColor Yellow }
}
Write-Host 'Öffne die neue .rrxp-Datei mit der installierten RRX-Version und prüfe die Zeichnungen.'
