# Veroeffentlicht YDownloader (ReadyToRun, win-x64) nach dist\ (nicht im Git):
#   dist\YDownloader-<version>-framework-dependent\   Ordner, braucht die .NET 8 Desktop Runtime
#   dist\YDownloader-<version>-framework-dependent.zip  derselbe Ordner als Zip
#   dist\YDownloader-<version>-self-contained.zip     bringt die Runtime mit
#   dist\SHA256SUMS.txt                               Pruefsummen beider Zips
# Aufruf: powershell -NoProfile -File tools\publish.ps1
# Die Version steht im csproj (<Version>). Die Extension-Dateien (Ordner "Extension") und die Sprachdateien (eingebettet)
# sind in beiden Ergebnissen enthalten.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$proj = Join-Path $root 'src\YDownloader\YDownloader.csproj'
$dist = Join-Path $root 'dist'
$version = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Keine <Version> im csproj.' }

$fd = Join-Path $dist "YDownloader-$version-framework-dependent"
$scDir = Join-Path $dist "_tmp\YDownloader-$version"
$scZip = Join-Path $dist "YDownloader-$version-self-contained.zip"
$fdZip = Join-Path $dist "YDownloader-$version-framework-dependent.zip"
foreach ($p in $fd, $scDir, $scZip, $fdZip, (Join-Path $dist 'SHA256SUMS.txt')) { if (Test-Path $p) { Remove-Item $p -Recurse -Force } }
New-Item -ItemType Directory -Force $dist | Out-Null

function Publish($out, $selfContained) {
    & dotnet publish $proj -c Release -r win-x64 --self-contained:$selfContained `
        -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false -o $out --nologo -v:m
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ist fehlgeschlagen ($out)" }
}

Publish $fd $false
Publish $scDir $true
# Compress-Archive (Windows PowerShell 5.1) schreibt Pfade mit "\" im Zip. ZipFile nimmt "/", und der Ordner
# "<Name>" oben im Zip verhindert, dass beim Entpacken alles lose im Zielordner landet.
Add-Type -AssemblyName System.IO.Compression.FileSystem
function ZipFolder($dir, $zipPath) {
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        $base = (Split-Path $dir -Parent).TrimEnd('\') + '\'
        foreach ($f in Get-ChildItem $dir -Recurse -File) {
            $name = $f.FullName.Substring($base.Length).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $zip.Dispose() }
}
ZipFolder $scDir $scZip
Remove-Item (Join-Path $dist '_tmp') -Recurse -Force
ZipFolder $fd $fdZip

# SHA256SUMS.txt: "<hash> *<Datei>", LF, UTF-8 ohne BOM (wie sha256sum)
$sums = foreach ($z in $fdZip, $scZip) { '{0} *{1}' -f (Get-FileHash $z -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $z -Leaf) }
[System.IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))

# Kurzpruefung: Version der exe und Extension-Ordner
foreach ($dir in $fd) {
    $exe = Join-Path $dir 'YDownloader.exe'
    $vi = (Get-Item $exe).VersionInfo
    Write-Host ("{0}: Datei {1}, Produkt {2}, Extension {3}" -f (Split-Path $dir -Leaf), $vi.FileVersion, $vi.ProductVersion,
        ((Test-Path (Join-Path $dir 'Extension\manifest.json')) -and (Test-Path (Join-Path $dir 'Extension\firefox\manifest.json'))))
}
foreach ($item in Get-ChildItem $dist) {
    $bytes = if ($item.PSIsContainer) { (Get-ChildItem $item.FullName -Recurse -File | Measure-Object Length -Sum).Sum } else { $item.Length }
    Write-Host ("{0}  {1:N1} MB" -f $item.Name, ($bytes / 1MB))
}
