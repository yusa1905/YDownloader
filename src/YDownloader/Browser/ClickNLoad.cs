using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace YDownloader.Browser;

/// <summary>
/// Click'n'Load: Auslesen von Formular, Schlüssel und verschlüsselter Linkliste. Reine Logik ohne Netzwerk. Webseiten schicken
/// <c>jk</c> als kleines JavaScript (<c>function f(){ return '3132…'; }</c>) und <c>crypted</c> als Base64 von AES-128-CBC
/// (Schlüssel und IV sind derselbe Hex-Wert, ohne Padding, mit Nullbytes aufgefüllt). Das JavaScript wird nie ausgeführt: Es
/// wird nur eine streng umrissene Form erkannt, aus der ein Hex-Wert gelesen wird, alles andere wird abgelehnt.
/// </summary>
public static class ClickNLoadCrypto
{
    /// <summary>Mehr als so viel Base64-Text nimmt der Entschlüssler nicht an.</summary>
    public const int MaxCryptedChars = 1_400_000;

    // Genau die Form, die Webseiten schicken: Funktion ohne Parameter, ein return mit einem Hex-Text in Anführungszeichen.
    private static readonly Regex FunctionRx = new(
        @"^\s*function\s+[A-Za-z_$][\w$]*\s*\(\s*\)\s*\{\s*return\s*(?<q>['""])(?<hex>[0-9A-Fa-f]{32,64})\k<q>\s*;?\s*\}\s*;?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex HexRx = new(@"^[0-9A-Fa-f]{32,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Liest den Schlüssel aus <c>jk</c>: entweder nur der Hex-Wert oder die Funktion in genau der üblichen Form. 16, 24 oder 32 Byte
    /// (32, 48 oder 64 Hex-Zeichen). Alles andere gibt false, es wird nie etwas ausgeführt.
    /// </summary>
    public static bool TryReadKey(string? jk, out byte[] key)
    {
        key = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(jk) || jk.Length > 512) return false;
        string? hex = null;
        try
        {
            string trimmed = jk.Trim();
            if (HexRx.IsMatch(trimmed)) hex = trimmed;
            else if (FunctionRx.Match(jk) is { Success: true } m) hex = m.Groups["hex"].Value;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        if (hex is null || (hex.Length != 32 && hex.Length != 48 && hex.Length != 64)) return false;
        key = Convert.FromHexString(hex);
        return true;
    }

    /// <summary>
    /// Entschlüsselt <c>crypted</c> mit dem Schlüssel (IV = die ersten 16 Byte des Schlüssels). Das Ergebnis ist der Text mit den
    /// Links, ohne die Nullbytes am Ende. Falsche Länge, ungültiges Base64 und kein gültiger UTF-8-Text geben false.
    /// </summary>
    public static bool TryDecrypt(string? crypted, byte[] key, out string text)
    {
        text = "";
        if (string.IsNullOrWhiteSpace(crypted) || crypted.Length > MaxCryptedChars) return false;
        if (key.Length is not (16 or 24 or 32)) return false;
        try
        {
            // Ein "+" im Formular wird zum Leerzeichen, Base64 hat dort ein "+".
            byte[] data = Convert.FromBase64String(crypted.Trim().Replace(' ', '+'));
            if (data.Length == 0 || data.Length % 16 != 0) return false;
            using var aes = Aes.Create();
            aes.Key = key;
            byte[] plain = aes.DecryptCbc(data, key.AsSpan(0, 16).ToArray(), PaddingMode.None);
            int end = plain.Length;
            while (end > 0 && plain[end - 1] == 0) end--;
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(plain, 0, end);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Gegenstück zum Entschlüsseln, nur für Tests mit selbst erzeugten Daten (Nullbytes bis zur Blockgröße).</summary>
    internal static string Encrypt(string text, byte[] key)
    {
        byte[] plain = Encoding.UTF8.GetBytes(text);
        Array.Resize(ref plain, (plain.Length + 15) / 16 * 16);
        using var aes = Aes.Create();
        aes.Key = key;
        return Convert.ToBase64String(aes.EncryptCbc(plain, key.AsSpan(0, 16).ToArray(), PaddingMode.None));
    }
}

/// <summary>Was eine Click'n'Load-Anfrage bringt: die Links und die mitgesendeten Archivpasswörter (Feld <c>passwords</c>).</summary>
public sealed record ClickNLoadSubmission(string Text, IReadOnlyList<string> Passwords)
{
    /// <summary>Höchstens so viele Passwörter je Anfrage.</summary>
    public const int MaxPasswords = 10;

    /// <summary>Eins je Zeile (ein Passwort darf Leerzeichen enthalten), bereinigt wie die Liste in den Einstellungen.</summary>
    public static List<string> ParsePasswords(string? field) =>
        Packages.PasswordRules.Normalize((field ?? "").Split('\n').Select(l => l.TrimEnd('\r')), MaxPasswords);
}

/// <summary>Formularkörper <c>application/x-www-form-urlencoded</c>: Name=Wert, mit &amp; getrennt.</summary>
public static class FormBody
{
    public static Dictionary<string, string> Parse(string body)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string name = Decode(eq < 0 ? pair : pair[..eq]);
            string value = eq < 0 ? "" : Decode(pair[(eq + 1)..]);
            fields.TryAdd(name, value); // bei doppelten Namen gilt der erste
        }
        return fields;
    }

    private static string Decode(string s)
    {
        try
        {
            return Uri.UnescapeDataString(s.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return s;
        }
    }
}
