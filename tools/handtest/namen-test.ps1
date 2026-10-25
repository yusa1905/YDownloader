# Handtest: Dateinamen aus Antwort und Adressen (Fund 3). Arbeitet nur in einer portablen Kopie unter C:\Temp\ydl-nametest
# und mit einem lokalen Testserver auf 127.0.0.1:8765, nie mit echten Daten, %AppData% oder echten Webseiten.
# Das echte YDownloader vorher beenden (Einzelinstanz).
#
#   powershell -ExecutionPolicy Bypass -File tools\handtest\namen-test.ps1 [-NoBuild] [-NoApp]
#
# Das Skript baut (ausser mit -NoBuild), startet die Kopie und dann den Testserver. Die Links unten in YDownloader einfuegen
# (Strg+V im Hinzufuegen-Dialog oder die Zwischenablage), hinzufuegen, laden lassen. Beenden mit Strg+C, danach zeigt das
# Skript, was im Zielordner liegt.
param(
    [switch]$NoBuild,
    [switch]$NoApp,
    [int]$Port = 8765
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$root = 'C:\Temp\ydl-nametest'
$app = Join-Path $root 'app'
$data = Join-Path $app 'Data'
$dl = Join-Path $root 'dl'
$base = "http://127.0.0.1:$Port"

if (-not $NoApp) {
    if (-not $NoBuild) {
        if (Test-Path $app) { Remove-Item $app -Recurse -Force }
        dotnet build (Join-Path $repo 'src\YDownloader\YDownloader.csproj') -c Release -o $app --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw 'Build fehlgeschlagen' }
    }
    if (-not (Test-Path (Join-Path $app 'YDownloader.exe'))) { throw "Keine Kopie in $app, ohne -NoBuild starten." }
    New-Item -ItemType File -Force (Join-Path $app '.portable') | Out-Null
    New-Item -ItemType Directory -Force $data, $dl | Out-Null
    foreach ($f in 'downloads.json', 'settings.json') { if (Test-Path (Join-Path $data $f)) { Remove-Item (Join-Path $data $f) -Force } }
    foreach ($f in Get-ChildItem $dl -File) { Remove-Item $f.FullName -Force }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $settings = '{"downloadFolder":"' + $dl.Replace('\', '\\') + '","autoExtract":false}'
    [IO.File]::WriteAllText((Join-Path $data 'settings.json'), $settings, $utf8)
    Start-Process (Join-Path $app 'YDownloader.exe') | Out-Null
}
New-Item -ItemType Directory -Force $dl | Out-Null

# Testdaten: Zufallsbytes (kein echtes Archiv) und eine kleine Webseite.
$rand = New-Object Random 7
$blob = New-Object byte[] (300KB); $rand.NextBytes($blob)
$html = [Text.Encoding]::UTF8.GetBytes('<html><body><h1>Das ist eine Webseite, keine Datei.</h1></body></html>')

# Pfad (ohne Abfrage) -> Antwort. Weiterleitung: Location gesetzt.
$routes = @{
    '/download.php'  = @{ Type = 'application/octet-stream'; Body = $blob }                       # ?file=setup.zip, kein Content-Disposition
    '/get/movie.mkv' = @{ Redirect = '/dl/8f3a9c' }                                               # Weiterleitung auf einen Pfad ohne Namen
    '/dl/8f3a9c'     = @{ Type = 'application/octet-stream'; Body = $blob }
    '/dl/zipnoext'   = @{ Type = 'application/zip'; Body = $blob }                                # keine Endung, Content-Type zip
    '/fake.zip'      = @{ Type = 'text/html; charset=utf-8'; Body = $html }                       # Webseite unter dem Namen .zip
    '/cd'            = @{ Type = 'application/octet-stream'; Body = $blob; Disposition = 'attachment; filename="mit-cd.zip"' }
}

$links = @(
    @{ Url = "$base/download.php?file=setup.zip"; Expect = 'setup.zip (Name aus der Abfrage)' },
    @{ Url = "$base/get/movie.mkv"; Expect = 'movie.mkv (Name der Original-Adresse bleibt nach der Weiterleitung)' },
    @{ Url = "$base/dl/zipnoext"; Expect = 'zipnoext.zip (Endung aus dem Content-Type)' },
    @{ Url = "$base/fake.zip"; Expect = 'FEHLER "Der Server lieferte eine Webseite statt der Datei", keine Datei im Ordner' },
    @{ Url = "$base/cd"; Expect = 'mit-cd.zip (Content-Disposition hat Vorrang)' }
)
Write-Host ''
Write-Host 'Links (alle auf einmal in YDownloader einfuegen, dann "Hinzufuegen"):'
$links | ForEach-Object { Write-Host ('  {0}' -f $_.Url) }
Write-Host ''
Write-Host 'Erwartet:'
$links | ForEach-Object { Write-Host ('  {0,-45} -> {1}' -f ($_.Url -replace [regex]::Escape($base), ''), $_.Expect) }
Write-Host ''
Write-Host 'Hinweis: Im Hinzufuegen-Dialog steht der Name oft schon nach der Probe richtig. Gehen die Namen nach dem Start'
Write-Host 'noch auf, prueft YDownloader sie einmal beim Start (geratene Namen werden ersetzt).'
$links.Url | Set-Clipboard
Write-Host '(Die Links liegen jetzt in der Zwischenablage.)'
Write-Host ''
Write-Host "Testserver laeuft auf $base. Beenden mit Strg+C."

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("$base/")
$listener.Start()
try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        $req = $ctx.Request
        $resp = $ctx.Response
        $path = $req.Url.AbsolutePath
        $route = $routes[$path]
        try {
            if (-not $route) {
                $resp.StatusCode = 404
            }
            elseif ($route.Redirect) {
                $resp.StatusCode = 302
                $resp.RedirectLocation = $route.Redirect
            }
            else {
                $resp.StatusCode = 200
                $resp.ContentType = $route.Type
                if ($route.Disposition) { $resp.AddHeader('Content-Disposition', $route.Disposition) }
                $resp.ContentLength64 = $route.Body.Length
                if ($req.HttpMethod -ne 'HEAD') { $resp.OutputStream.Write($route.Body, 0, $route.Body.Length) }
            }
        }
        catch { }
        finally { try { $resp.Close() } catch { } }
        Write-Host ('{0:HH:mm:ss} {1} {2} -> {3}' -f (Get-Date), $req.HttpMethod, $req.Url.PathAndQuery, $resp.StatusCode)
    }
}
finally {
    $listener.Stop()
    Write-Host ''
    Write-Host "Im Zielordner $dl liegt:"
    Get-ChildItem $dl -File -ErrorAction SilentlyContinue | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Host
}
