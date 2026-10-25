# Handtest: gesperrte und kaputte Datendateien. Arbeitet nur in einer portablen Kopie unter C:\Temp\ydl-datatest,
# nie in %AppData% und nie in echten Daten. Das echte YDownloader vorher beenden (Einzelinstanz).
#
#   powershell -File tools\handtest\daten-sicherheit.ps1 -Scenario <name> [-NoBuild]
#
# Szenarien:
#   downloads-locked    downloads.json gesperrt (wie Virenscanner). Erwartet: Hinweis "Daten nicht lesbar", leere Liste,
#                       Datei bleibt beim Beenden unveraendert (Groesse/Hash werden am Ende verglichen).
#   downloads-corrupt   downloads.json kaputt. Erwartet: Hinweis "Datei war beschaedigt", downloads.json.bad vorhanden.
#   settings-locked     settings.json gesperrt. Erwartet: Hinweis, Vorgaben; "Uebernehmen" im Einstellungs-Sheet meldet
#                       den Fehler und schliesst nicht; die Datei bleibt unveraendert.
#   settings-corrupt    settings.json kaputt. Erwartet: Hinweis, settings.json.bad vorhanden, Vorgaben.
#   settings-writefail  settings.json ist gut, aber ein Ordner "settings.json.tmp" blockiert das Schreiben. Erwartet:
#                       Einstellungen oeffnen, etwas aendern, "Uebernehmen": rote Fehlermeldung im Sheet, Sheet bleibt offen.
#   normal              Beide Dateien gut (Kontrolle: kein Hinweis).
param(
    [Parameter(Mandatory)]
    [ValidateSet('downloads-locked', 'downloads-corrupt', 'settings-locked', 'settings-corrupt', 'settings-writefail', 'normal')]
    [string]$Scenario,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$root = 'C:\Temp\ydl-datatest'
$app = Join-Path $root 'app'
$data = Join-Path $app 'Data'

if (-not $NoBuild) {
    if (Test-Path $app) { Remove-Item $app -Recurse -Force }
    dotnet build (Join-Path $repo 'src\YDownloader\YDownloader.csproj') -c Release -o $app --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Build fehlgeschlagen' }
}
if (-not (Test-Path (Join-Path $app 'YDownloader.exe'))) { throw "Keine Kopie in $app, ohne -NoBuild starten." }

# Portable Kopie mit frischen, ausgedachten Daten.
New-Item -ItemType File -Force (Join-Path $app '.portable') | Out-Null
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
New-Item -ItemType Directory -Force $data | Out-Null
$dl = Join-Path $data 'downloads.json'
$st = Join-Path $data 'settings.json'
$dlJson = '{"version":1,"items":[{"id":"a1","url":"http://example.invalid/beispiel.bin","fileName":"beispiel.bin","folder":"C:\\Temp\\ydl-datatest\\dl","status":"Paused","totalBytes":1000,"downloadedBytes":100}]}'
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($dl, $dlJson, $utf8)
[IO.File]::WriteAllText($st, '{"maxConcurrent":4}', $utf8)

$hold = $null
switch ($Scenario) {
    'downloads-locked'   { $hold = New-Object IO.FileStream($dl, 'Open', 'Read', 'None') }
    'downloads-corrupt'  { [IO.File]::WriteAllText($dl, '{ kaputt', $utf8) }
    'settings-locked'    { $hold = New-Object IO.FileStream($st, 'Open', 'Read', 'None') }
    'settings-corrupt'   { [IO.File]::WriteAllText($st, '{ "maxConcurrent": ', $utf8) }
    'settings-writefail' { New-Item -ItemType Directory (Join-Path $data 'settings.json.tmp') | Out-Null }
}

$before = @{}
foreach ($f in $dl, $st) { if (-not $hold -or $f -ne $hold.Name) { $before[$f] = (Get-FileHash $f).Hash } }
$sizeBefore = (Get-Item $dl).Length, (Get-Item $st).Length

Write-Host "Szenario: $Scenario"
Write-Host "Daten:    $data"
Write-Host 'Starte YDownloader. Beenden mit Strg+Q (oder Tray > Beenden), dann hier Enter druecken.'
$proc = Start-Process (Join-Path $app 'YDownloader.exe') -PassThru
[void](Read-Host 'Enter, wenn YDownloader beendet ist')
if ($hold) { $hold.Dispose() }

Write-Host ''
Write-Host 'Nach dem Beenden:'
foreach ($f in $dl, $st) {
    $name = Split-Path $f -Leaf
    $now = (Get-FileHash $f).Hash
    $same = if ($before.ContainsKey($f)) { if ($before[$f] -eq $now) { 'unveraendert' } else { 'VERAENDERT' } } else { '(war gesperrt) Hash ' + $now.Substring(0, 12) }
    Write-Host ("  {0,-15} {1}" -f $name, $same)
}
foreach ($bad in "$dl.bad", "$st.bad") { if (Test-Path $bad) { Write-Host ("  {0,-15} vorhanden ({1} Bytes)" -f (Split-Path $bad -Leaf), (Get-Item $bad).Length) } }
Write-Host ("  Groessen vorher: downloads {0}, settings {1}" -f $sizeBefore[0], $sizeBefore[1])
Write-Host ("  Groessen jetzt:  downloads {0}, settings {1}" -f (Get-Item $dl).Length, (Get-Item $st).Length)
