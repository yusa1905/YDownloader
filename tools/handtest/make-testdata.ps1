# Legt unter C:\Temp\ydl-test\serve selbst erzeugte Testarchive an (7-Zip aus Scoop). Keine echten Daten.
$ErrorActionPreference = 'Stop'
$root  = 'C:\Temp\ydl-test'
$serve = Join-Path $root 'serve'
$work  = Join-Path $root 'work'

$sz = (Get-Command 7z -ErrorAction SilentlyContinue).Source
if (-not $sz) { throw '7z nicht im PATH gefunden (Scoop: scoop install 7zip).' }

foreach ($d in $serve, $work) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Force $d | Out-Null
}

# Quelldateien: ein paar Textdateien und 2,5 MB unkomprimierbare Zufallsdaten
$src = Join-Path $work 'src'
New-Item -ItemType Directory -Force (Join-Path $src 'ordner') | Out-Null
Set-Content (Join-Path $src 'hallo.txt') 'Hallo aus dem Testarchiv' -Encoding utf8
Set-Content (Join-Path $src 'ordner\umlaute.txt') 'Grüße, Äpfel, Öl' -Encoding utf8
$bytes = New-Object byte[] (2.5MB)
(New-Object Random 42).NextBytes($bytes)
[IO.File]::WriteAllBytes((Join-Path $src 'zufall.bin'), $bytes)

# 1) kleines ZIP
& $sz a -tzip (Join-Path $serve 'klein.zip') (Join-Path $src 'hallo.txt') (Join-Path $src 'ordner') | Out-Null

# 2) 3-teiliges 7z, ohne Kompression, Teile zu 1 MB
& $sz a -t7z -mx0 -v1m (Join-Path $serve 'paket.7z') (Join-Path $src 'zufall.bin') (Join-Path $src 'hallo.txt') | Out-Null

# 3) passwortgeschütztes 7z (Passwort: geheim123)
& $sz a -t7z -pgeheim123 (Join-Path $serve 'geheim.7z') (Join-Path $src 'hallo.txt') | Out-Null

# 4) 3-teiliges passwortgeschütztes 7z (Passwort: geheim123), ohne Kompression, Teile zu 1 MB
& $sz a -t7z -mx0 -v1m -pgeheim123 (Join-Path $serve 'paket-pw.7z') (Join-Path $src 'zufall.bin') (Join-Path $src 'hallo.txt') | Out-Null

Remove-Item $work -Recurse -Force
Get-ChildItem $serve | Select-Object Name, @{n='KB';e={[math]::Round($_.Length/1KB)}}
