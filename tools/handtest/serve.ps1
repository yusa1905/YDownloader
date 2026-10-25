# Stellt C:\Temp\ydl-test nur auf 127.0.0.1:8099 bereit (TcpListener, keine Adminrechte, mit Range-Unterstützung).
# Die festen Testdateien liegen in ...\serve und sind unter /<name> erreichbar, alle anderen Dateien unter ihrem Pfad ab dem Ordner.
# Pfade mit .. oder außerhalb des Ordners gibt es nicht (404). Beenden mit Strg+C.
$root  = 'C:\Temp\ydl-test'
$known = Join-Path $root 'serve'
$dir   = $root
$port  = 8099
if (-not (Test-Path $root)) { throw "Erst make-testdata.ps1 ausführen ($root fehlt)." }

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public static class LocalServe
{
    public static TcpListener Start(string dir, int port)
    {
        var l = new TcpListener(IPAddress.Loopback, port);
        l.Start();
        var t = new Thread(delegate () {
            while (true)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); } catch (Exception) { return; }
                ThreadPool.QueueUserWorkItem(delegate (object o) { Handle((TcpClient)o, dir); }, c);
            }
        });
        t.IsBackground = true;
        t.Start();
        return l;
    }

    static void Handle(TcpClient c, string dir)
    {
        using (c)
        {
            try
            {
                var s = c.GetStream();
                var r = new StreamReader(s, Encoding.ASCII);
                string line = r.ReadLine();
                if (line == null) return;
                string[] p = line.Split(' ');
                long from = -1, to = -1;
                string h;
                while (!string.IsNullOrEmpty(h = r.ReadLine()))
                {
                    if (h.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] v = h.Substring(13).Split('-');
                        if (v[0].Length > 0) from = long.Parse(v[0]);
                        if (v.Length > 1 && v[1].Length > 0) to = long.Parse(v[1]);
                    }
                }
                string name = Uri.UnescapeDataString((p.Length > 1 ? p[1] : "").Split('?')[0]).TrimStart('/');
                string path = Resolve(dir, name);
                if (path == null || !File.Exists(path)) { Send(s, "404 Not Found", 0, null, ""); return; }

                long len = new FileInfo(path).Length;
                bool head = p[0] == "HEAD";
                if (from >= 0)
                {
                    if (to < 0 || to >= len) to = len - 1;
                    if (from > to) { Send(s, "416 Range Not Satisfiable", 0, null, "Content-Range: bytes */" + len + "\r\n"); return; }
                    Send(s, "206 Partial Content", to - from + 1, head ? null : path,
                        "Content-Range: bytes " + from + "-" + to + "/" + len + "\r\n", from);
                }
                else Send(s, "200 OK", len, head ? null : path, "", 0);
            }
            catch (Exception) { }
        }
    }

    // Erst die festen Testdateien (dir\serve), dann jede Datei unter dir. Nie außerhalb von dir.
    static string Resolve(string dir, string name)
    {
        if (name.Length == 0 || name.IndexOfAny(new[] { '\\', ':', '\0' }) >= 0) return null;
        foreach (string seg in name.Split('/'))
            if (seg.Length == 0 || seg == "." || seg == ".." || seg.TrimEnd('.', ' ').Length == 0) return null;
        string root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        foreach (string baseDir in new[] { Path.Combine(root, "serve"), root })
        {
            string full = Path.GetFullPath(Path.Combine(baseDir, name.Replace('/', '\\')));
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) return full;
        }
        return null;
    }

    static void Send(NetworkStream s, string status, long length, string path, string extra, long offset = 0)
    {
        string head = "HTTP/1.1 " + status + "\r\nContent-Length: " + length + "\r\nAccept-Ranges: bytes\r\n"
            + "Content-Type: application/octet-stream\r\n" + extra + "Connection: close\r\n\r\n";
        byte[] hb = Encoding.ASCII.GetBytes(head);
        s.Write(hb, 0, hb.Length);
        if (path == null) return;
        using (var f = File.OpenRead(path))
        {
            f.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[65536];
            long left = length;
            while (left > 0)
            {
                int n = f.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (n <= 0) break;
                s.Write(buf, 0, n);
                left -= n;
            }
        }
    }
}
'@

$listener = [LocalServe]::Start($dir, $port)
Write-Host "Läuft auf 127.0.0.1:$port (Strg+C beendet). Diese URLs ins Hinzufügen-Sheet einfügen:`n"
# Die drei Teile des 7z gehören zusammen und bilden ein Paket.
$knownNames = @()
if (Test-Path $known) {
    Get-ChildItem $known -File | Sort-Object Name | ForEach-Object {
        $knownNames += $_.Name
        "http://127.0.0.1:$port/$([uri]::EscapeDataString($_.Name))"
    }
}
$rootFull = (Resolve-Path $root).Path.TrimEnd('\') + '\'
$others = Get-ChildItem $root -File -Recurse | ForEach-Object {
    $rel = $_.FullName.Substring($rootFull.Length)
    if ($rel.StartsWith('serve\', [StringComparison]::OrdinalIgnoreCase)) { return }
    if ($knownNames -contains $rel) { return }   # gleicher Name wie eine feste Testdatei: die feste gewinnt
    $rel
} | Sort-Object
if ($others) {
    Write-Host "`nWeitere Dateien im Ordner:"
    $others | ForEach-Object { "http://127.0.0.1:$port/" + (($_ -split '\\' | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/') }
}
Write-Host "`nPasswort von geheim.7z: geheim123 (die App fragt nicht, es soll 'Passwort nötig' erscheinen)."
try { while ($true) { Start-Sleep 1 } } finally { $listener.Stop() }
