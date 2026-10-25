using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using YDownloader.Browser;

namespace YDownloader.Tests;

/// <summary>
/// Click'n'Load mit selbst erzeugten Daten: Schlüssel und Links sind ausgedacht, die Links zeigen auf reservierte Testadressen
/// (<c>.invalid</c>, <c>127.0.0.1</c>). Der Server läuft auf einem freien Port von 127.0.0.1.
/// </summary>
public class ClickNLoadTests : IDisposable
{
    private const string Links = "http://files.example.invalid/a/Film.part1.rar\r\nhttp://127.0.0.1:1/b/Film.part2.rar";
    private static readonly byte[] Key = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
    private static readonly string KeyHex = Convert.ToHexString(Key).ToLowerInvariant();

    private readonly ClickNLoadServer _server = ClickNLoadServer.Start(0);
    private readonly ConcurrentQueue<string> _received = new();

    private readonly ConcurrentQueue<ClickNLoadSubmission> _submissions = new();

    public ClickNLoadTests() => _server.LinksReceived += s =>
    {
        _submissions.Enqueue(s);
        _received.Enqueue(s.Text);
    };

    public void Dispose() => _server.Dispose();

    // ---------------------------------------------------------------- Schlüssel (kein JavaScript)

    [Theory]
    [InlineData("00112233445566778899aabbccddeeff")]
    [InlineData("  00112233445566778899AABBCCDDEEFF \n")]
    [InlineData("function f(){ return '00112233445566778899aabbccddeeff'; }")]
    [InlineData("function f() { return \"00112233445566778899aabbccddeeff\"; }")]
    [InlineData("function jk(){return'00112233445566778899aabbccddeeff'}")]
    [InlineData("\nfunction f(){\n  return '00112233445566778899aabbccddeeff';\n}\n")]
    public void Key_is_read_from_hex_or_the_usual_function(string jk)
    {
        Assert.True(ClickNLoadCrypto.TryReadKey(jk, out byte[] key));
        Assert.Equal(Key, key);
    }

    [Fact]
    public void Longer_keys_are_accepted_but_only_whole_bytes_of_AES_sizes()
    {
        Assert.True(ClickNLoadCrypto.TryReadKey(new string('a', 48), out var k24));
        Assert.Equal(24, k24.Length);
        Assert.True(ClickNLoadCrypto.TryReadKey(new string('b', 64), out var k32));
        Assert.Equal(32, k32.Length);
        Assert.False(ClickNLoadCrypto.TryReadKey(new string('a', 40), out _));
        Assert.False(ClickNLoadCrypto.TryReadKey(new string('a', 30), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("zz112233445566778899aabbccddeeff")]
    [InlineData("function f(){ return '00112233445566778899aabbccddeeff' + alert(1); }")]
    [InlineData("function f(){ alert(1); return '00112233445566778899aabbccddeeff'; }")]
    [InlineData("function f(){ return '00112233445566778899aabbccddeeff'; } alert(1)")]
    [InlineData("function f(){ return '00112233445566778899aabbccddeeff\"; }")]
    [InlineData("function f(){ return (function(){ return '00112233445566778899aabbccddeeff'; })(); }")]
    [InlineData("function f(x){ return '00112233445566778899aabbccddeeff'; }")]
    [InlineData("function f(){ return 0x00112233445566778899aabbccddeeff; }")]
    [InlineData("var jk = '00112233445566778899aabbccddeeff';")]
    [InlineData("eval('00112233445566778899aabbccddeeff')")]
    public void Key_from_anything_else_is_rejected_and_nothing_runs(string? jk)
        => Assert.False(ClickNLoadCrypto.TryReadKey(jk, out _));

    [Fact]
    public void Huge_jk_is_rejected_quickly()
    {
        string jk = "function f(){ return '" + new string('a', 100_000) + "'; }";
        Assert.False(ClickNLoadCrypto.TryReadKey(jk, out _));
    }

    // ---------------------------------------------------------------- AES-CBC

    [Fact]
    public void Decrypts_what_was_encrypted_with_zero_padding_and_strips_it()
    {
        string crypted = ClickNLoadCrypto.Encrypt(Links, Key);

        Assert.True(ClickNLoadCrypto.TryDecrypt(crypted, Key, out string text));
        Assert.Equal(Links, text);
    }

    [Fact]
    public void Decrypts_non_ascii_text_and_exact_block_lengths()
    {
        string exact = new string('x', 32);
        Assert.True(ClickNLoadCrypto.TryDecrypt(ClickNLoadCrypto.Encrypt(exact, Key), Key, out string t1));
        Assert.Equal(exact, t1);

        string umlauts = "http://127.0.0.1:1/Bücher/ä.rar";
        Assert.True(ClickNLoadCrypto.TryDecrypt(ClickNLoadCrypto.Encrypt(umlauts, Key), Key, out string t2));
        Assert.Equal(umlauts, t2);
    }

    [Fact]
    public void Known_answer_for_a_fixed_key_and_text()
    {
        // 16 Byte Klartext, Schlüssel = IV, ein Block: unabhängig vom eigenen Verschlüsseln nachgerechnet.
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = Key;
        byte[] expected = aes.EncryptCbc(Encoding.ASCII.GetBytes("http://127.0.0.1"), Key, System.Security.Cryptography.PaddingMode.None);

        Assert.True(ClickNLoadCrypto.TryDecrypt(Convert.ToBase64String(expected), Key, out string text));
        Assert.Equal("http://127.0.0.1", text);
    }

    [Fact]
    public void Plus_signs_turned_into_spaces_by_the_form_are_repaired()
    {
        string crypted = ClickNLoadCrypto.Encrypt(Links + "\r\nhttp://127.0.0.1:1/c" + new string('+', 3), Key);
        string asForm = crypted.Replace('+', ' ');

        Assert.True(ClickNLoadCrypto.TryDecrypt(asForm, Key, out string text));
        Assert.StartsWith(Links, text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("%%%not base64")]
    [InlineData("AAAA")]                                  // keine Blocklänge
    public void Broken_crypted_text_is_rejected(string? crypted)
        => Assert.False(ClickNLoadCrypto.TryDecrypt(crypted, Key, out _));

    [Fact]
    public void Wrong_key_gives_garbage_that_is_rejected_as_text_or_has_no_links()
    {
        string crypted = ClickNLoadCrypto.Encrypt(Links, Key);
        byte[] other = Convert.FromHexString("FFEEDDCCBBAA99887766554433221100");

        bool ok = ClickNLoadCrypto.TryDecrypt(crypted, other, out string text);

        Assert.True(!ok || !text.Contains("http://"));
    }

    [Fact]
    public void Wrong_key_length_is_rejected()
        => Assert.False(ClickNLoadCrypto.TryDecrypt(ClickNLoadCrypto.Encrypt(Links, Key), new byte[10], out _));

    [Fact]
    public void Oversized_crypted_text_is_rejected()
        => Assert.False(ClickNLoadCrypto.TryDecrypt(new string('A', ClickNLoadCrypto.MaxCryptedChars + 4), Key, out _));

    [Fact]
    public void Form_is_decoded_with_plus_percent_and_first_value_wins()
    {
        var form = FormBody.Parse("a=1&b=x+y%26z&a=2&c&d=%C3%A4&e=%ZZ");

        Assert.Equal("1", form["a"]);
        Assert.Equal("x y&z", form["b"]);
        Assert.Equal("", form["c"]);
        Assert.Equal("ä", form["d"]);
        Assert.Equal("%ZZ", form["e"]);
    }

    // ---------------------------------------------------------------- Server

    private async Task<string> SendAsync(string raw, int? port = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port ?? _server.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(raw));
        using var ms = new System.IO.MemoryStream();
        await stream.CopyToAsync(ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string Post(string path, string form, string? host = null, string type = "application/x-www-form-urlencoded") =>
        $"POST {path} HTTP/1.1\r\nHost: {host ?? "127.0.0.1:" + _server.Port}\r\nContent-Type: {type}\r\n" +
        $"Content-Length: {Encoding.UTF8.GetByteCount(form)}\r\n\r\n{form}";

    private static string Status(string response) => response.Split("\r\n")[0];

    private async Task<string> WaitReceived()
    {
        for (int i = 0; i < 500 && _received.IsEmpty; i++) await Task.Delay(10);
        Assert.True(_received.TryDequeue(out string? text));
        return text!;
    }

    [Fact]
    public async Task Get_flash_answers_with_the_service_name_and_cors()
    {
        string response = await SendAsync($"GET /flash HTTP/1.1\r\nHost: 127.0.0.1:{_server.Port}\r\n\r\n");

        Assert.Equal("HTTP/1.1 200 OK", Status(response));
        Assert.EndsWith("JDownloader", response);
        Assert.Contains("Access-Control-Allow-Origin: *", response);
    }

    [Fact]
    public async Task Preflight_gets_the_cors_headers_including_private_network_access()
    {
        string response = await SendAsync(
            $"OPTIONS /flash/addcrypted2 HTTP/1.1\r\nHost: localhost:{_server.Port}\r\nOrigin: http://web.example.invalid\r\n" +
            "Access-Control-Request-Method: POST\r\nAccess-Control-Request-Headers: content-type\r\n" +
            "Access-Control-Request-Private-Network: true\r\n\r\n");

        Assert.Equal("HTTP/1.1 204 No Content", Status(response));
        Assert.Contains("Access-Control-Allow-Origin: *", response);
        Assert.Contains("Access-Control-Allow-Methods: GET, POST, OPTIONS", response);
        Assert.Contains("Access-Control-Allow-Headers: content-type", response);
        Assert.Contains("Access-Control-Allow-Private-Network: true", response);
    }

    [Fact]
    public async Task Preflight_does_not_echo_odd_header_lists()
    {
        string response = await SendAsync(
            $"OPTIONS /flash/add HTTP/1.1\r\nHost: 127.0.0.1:{_server.Port}\r\nAccess-Control-Request-Headers: x<script>\r\n\r\n");

        Assert.Contains("Access-Control-Allow-Headers: Content-Type\r\n", response);
        Assert.DoesNotContain("<script>", response);
    }

    [Fact]
    public async Task Add_passes_the_urls_on_and_never_starts_anything_itself()
    {
        string response = await SendAsync(Post("/flash/add", "package=P&passwords=&source=x&urls=" + Uri.EscapeDataString(Links)));

        Assert.Equal("HTTP/1.1 200 OK", Status(response));
        Assert.EndsWith("success\r\n", response);
        Assert.Equal(Links, await WaitReceived());
    }

    [Fact]
    public async Task Passwords_field_is_delivered_with_the_links()
    {
        string response = await SendAsync(Post("/flash/add", "passwords=" + Uri.EscapeDataString("geheim 123") + "&urls=" + Uri.EscapeDataString(Links)));

        Assert.Equal("HTTP/1.1 200 OK", Status(response));
        await WaitReceived();
        Assert.True(_submissions.TryPeek(out var s));
        Assert.Equal(new[] { "geheim 123" }, s!.Passwords);
    }

    [Fact]
    public async Task Passwords_also_come_with_encrypted_links_and_empty_field_gives_none()
    {
        string form = "passwords=" + Uri.EscapeDataString("eins\r\nzwei") + "&jk=" + Uri.EscapeDataString($"function f(){{ return '{KeyHex}'; }}") +
                      "&crypted=" + Uri.EscapeDataString(ClickNLoadCrypto.Encrypt(Links, Key));
        await SendAsync(Post("/flash/addcrypted2", form));
        await WaitReceived();
        Assert.True(_submissions.TryDequeue(out var s));
        Assert.Equal(new[] { "eins", "zwei" }, s!.Passwords);

        await SendAsync(Post("/flash/add", "passwords=&urls=" + Uri.EscapeDataString(Links)));
        await WaitReceived();
        Assert.True(_submissions.TryDequeue(out s));
        Assert.Empty(s!.Passwords);
    }

    [Fact]
    public void ParsePasswords_cleans_dedupes_and_caps()
    {
        Assert.Empty(ClickNLoadSubmission.ParsePasswords(null));
        Assert.Equal(new[] { "a b", "c" }, ClickNLoadSubmission.ParsePasswords(" a b \r\n\r\nc\na b"));
        var many = string.Join("\n", Enumerable.Range(0, 40).Select(i => "p" + i));
        Assert.Equal(ClickNLoadSubmission.MaxPasswords, ClickNLoadSubmission.ParsePasswords(many).Count);
        Assert.Empty(ClickNLoadSubmission.ParsePasswords(new string('x', 300)));
    }

    [Fact]
    public async Task Addcrypted2_decrypts_with_the_key_from_jk()
    {
        string form = "passwords=&source=&package=P&jk=" + Uri.EscapeDataString($"function f(){{ return '{KeyHex}'; }}") +
                      "&crypted=" + Uri.EscapeDataString(ClickNLoadCrypto.Encrypt(Links, Key));

        string response = await SendAsync(Post("/flash/addcrypted2", form));

        Assert.Equal("HTTP/1.1 200 OK", Status(response));
        Assert.Equal(Links, await WaitReceived());
    }

    [Fact]
    public async Task Addcrypted2_also_works_when_the_form_turns_plus_into_space()
    {
        // Eine Webseite, die Base64 nicht prozentkodiert: das "+" kommt als Leerzeichen an.
        string crypted;
        int i = 0;
        do { crypted = ClickNLoadCrypto.Encrypt(Links + new string('/', i++), Key); } while (!crypted.Contains('+') && i < 200);
        Assert.Contains('+', crypted);
        string form = "jk=" + KeyHex + "&crypted=" + crypted.Replace('+', ' ').Replace("=", "%3D");

        string response = await SendAsync(Post("/flash/addcrypted2", form));

        Assert.Equal("HTTP/1.1 200 OK", Status(response));
        Assert.StartsWith(Links, await WaitReceived());
    }

    [Theory]
    [InlineData("jk=function f(){ alert(1); }&crypted=AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("jk=00112233445566778899aabbccddeeff&crypted=%25%25")]
    [InlineData("crypted=AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("jk=00112233445566778899aabbccddeeff")]
    public async Task Addcrypted2_with_bad_fields_is_refused_and_passes_nothing_on(string form)
    {
        string response = await SendAsync(Post("/flash/addcrypted2", form));

        Assert.Equal("HTTP/1.1 400 Bad Request", Status(response));
        Assert.EndsWith("failed", response);
        Assert.True(_received.IsEmpty);
    }

    [Fact]
    public async Task A_text_without_links_is_refused()
    {
        string form = "jk=" + KeyHex + "&crypted=" + Uri.EscapeDataString(ClickNLoadCrypto.Encrypt("keine links hier", Key));

        Assert.Equal("HTTP/1.1 400 Bad Request", Status(await SendAsync(Post("/flash/addcrypted2", form))));
        Assert.Equal("HTTP/1.1 400 Bad Request", Status(await SendAsync(Post("/flash/add", "urls=nichts"))));
        Assert.True(_received.IsEmpty);
    }

    [Fact]
    public async Task Other_hosts_in_the_host_header_are_refused_against_dns_rebinding()
    {
        string response = await SendAsync(Post("/flash/add", "urls=" + Uri.EscapeDataString(Links), host: "evil.example.invalid:" + _server.Port));

        Assert.Equal("HTTP/1.1 421 Misdirected Request", Status(response));
        Assert.True(_received.IsEmpty);
    }

    [Fact]
    public async Task A_wrong_port_or_missing_host_is_refused()
    {
        Assert.Equal("HTTP/1.1 421 Misdirected Request",
            Status(await SendAsync(Post("/flash/add", "urls=" + Uri.EscapeDataString(Links), host: "127.0.0.1:1"))));
        Assert.Equal("HTTP/1.1 421 Misdirected Request",
            Status(await SendAsync("GET /flash HTTP/1.1\r\n\r\n")));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    public async Task Loopback_names_are_accepted_with_or_without_port(string host)
    {
        string port = host + ":" + _server.Port;
        Assert.Equal("HTTP/1.1 200 OK", Status(await SendAsync($"GET /flash HTTP/1.1\r\nHost: {port}\r\n\r\n")));
    }

    [Fact]
    public async Task Wrong_content_type_and_unknown_paths_are_refused()
    {
        Assert.Equal("HTTP/1.1 415 Unsupported Media Type",
            Status(await SendAsync(Post("/flash/add", "urls=" + Links, type: "text/plain"))));
        Assert.Equal("HTTP/1.1 404 Not Found", Status(await SendAsync(Post("/flash/addcrypted", "x=1"))));
        Assert.Equal("HTTP/1.1 404 Not Found", Status(await SendAsync($"GET /other HTTP/1.1\r\nHost: 127.0.0.1:{_server.Port}\r\n\r\n")));
        Assert.Equal("HTTP/1.1 404 Not Found", Status(await SendAsync($"DELETE /flash/add HTTP/1.1\r\nHost: 127.0.0.1:{_server.Port}\r\n\r\n")));
        Assert.True(_received.IsEmpty);
    }

    [Fact]
    public async Task Body_above_the_limit_is_refused_without_reading_it()
    {
        string response = await SendAsync(
            $"POST /flash/add HTTP/1.1\r\nHost: 127.0.0.1:{_server.Port}\r\nContent-Type: application/x-www-form-urlencoded\r\n" +
            $"Content-Length: {ClickNLoadServer.MaxBodyBytes + 1}\r\n\r\n");

        Assert.Equal("HTTP/1.1 413 Payload Too Large", Status(response));
    }

    [Fact]
    public async Task A_body_at_the_limit_is_read()
    {
        string pad = new string('x', ClickNLoadServer.MaxBodyBytes - 200);
        string form = "a=" + pad + "&urls=" + Uri.EscapeDataString("http://127.0.0.1:1/x");
        Assert.True(Encoding.UTF8.GetByteCount(form) <= ClickNLoadServer.MaxBodyBytes);

        Assert.Equal("HTTP/1.1 200 OK", Status(await SendAsync(Post("/flash/add", form))));
        Assert.Equal("http://127.0.0.1:1/x", await WaitReceived());
    }

    [Fact]
    public async Task A_huge_header_is_refused()
    {
        // Der Server antwortet und schließt, bevor der Client fertig gesendet hat: Je nach Timing sieht der Client die Antwort
        // oder nur das Zurücksetzen der Verbindung. Beides ist richtig, die Anfrage darf nur nichts bewirken.
        try
        {
            string response = await SendAsync(
                $"GET /flash HTTP/1.1\r\nHost: 127.0.0.1:{_server.Port}\r\nX-Pad: {new string('a', ClickNLoadServer.MaxHeaderBytes)}\r\n\r\n");
            Assert.Equal("HTTP/1.1 431 Request Header Fields Too Large", Status(response));
        }
        catch (Exception ex) when (ex is System.IO.IOException or SocketException)
        {
        }

        Assert.Equal("HTTP/1.1 200 OK", Status(await SendAsync($"GET /flash HTTP/1.1\r\nHost: 127.0.0.1:{_server.Port}\r\n\r\n")));
    }

    [Fact]
    public async Task Chunked_bodies_and_garbage_are_refused()
    {
        string chunked = $"POST /flash/add HTTP/1.1\r\nHost: 127.0.0.1:{_server.Port}\r\nContent-Type: application/x-www-form-urlencoded\r\n" +
                         "Transfer-Encoding: chunked\r\n\r\n0\r\n\r\n";
        Assert.Equal("HTTP/1.1 411 Length Required", Status(await SendAsync(chunked)));
        Assert.Equal("HTTP/1.1 400 Bad Request", Status(await SendAsync("hallo welt\r\n\r\n")));
        Assert.Equal("HTTP/1.1 400 Bad Request",
            Status(await SendAsync($"GET /flash HTTP/1.1\r\nHost: 127.0.0.1:{_server.Port}\r\nkein-doppelpunkt\r\n\r\n")));
    }

    [Fact]
    public async Task Submissions_per_minute_are_limited()
    {
        string request = Post("/flash/add", "urls=" + Uri.EscapeDataString("http://127.0.0.1:1/x"));
        for (int i = 0; i < ClickNLoadServer.MaxSubmissionsPerMinute; i++)
            Assert.Equal("HTTP/1.1 200 OK", Status(await SendAsync(request)));

        Assert.Equal("HTTP/1.1 429 Too Many Requests", Status(await SendAsync(request)));
    }

    [Fact]
    public async Task Server_listens_on_the_loopback_address_only()
    {
        var local = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (local is null) return; // kein anderes Netz auf diesem Rechner: nichts zu prüfen

        using var client = new TcpClient();
        bool connected;
        try
        {
            await client.ConnectAsync(local, _server.Port).WaitAsync(TimeSpan.FromSeconds(3));
            connected = client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            connected = false; // abgelehnt oder nicht erreichbar: so soll es sein
        }
        Assert.False(connected);
    }

    [Fact]
    public void A_busy_port_is_reported_as_address_in_use()
    {
        var ex = Assert.Throws<SocketException>(() => ClickNLoadServer.Start(_server.Port));

        Assert.Equal(SocketError.AddressAlreadyInUse, ex.SocketErrorCode);
    }

    [Fact]
    public async Task Server_stops_on_dispose_and_frees_the_port()
    {
        int port = _server.Port;
        _server.Dispose();

        // Der Port wird nach dem Schließen nicht immer sofort frei: auf die Bedingung warten statt fest 50 ms.
        ClickNLoadServer? again = null;
        for (int i = 0; i < 100 && again is null; i++)
        {
            try { again = ClickNLoadServer.Start(port); }
            catch (SocketException) { await Task.Delay(50); }
        }
        Assert.NotNull(again);
        using (again) Assert.Equal(port, again!.Port);
    }

    [Fact]
    public async Task A_slow_client_does_not_block_others()
    {
        using var slow = new TcpClient();
        await slow.ConnectAsync(IPAddress.Loopback, _server.Port);
        await slow.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /flash HT")); // unvollständig, hängt

        string response = await SendAsync($"GET /flash HTTP/1.1\r\nHost: 127.0.0.1:{_server.Port}\r\n\r\n");

        Assert.Equal("HTTP/1.1 200 OK", Status(response));
    }
}
