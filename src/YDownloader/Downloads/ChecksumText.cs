using System;
using YDownloader.Engine;

namespace YDownloader.Downloads;

/// <summary>Prüfsumme als Text: <c>sha256:ab12…</c>, oder nur Hex, dann entscheidet die Länge (32 MD5, 40 SHA-1, 64 SHA-256).</summary>
public static class ChecksumText
{
    public static ChecksumSpec? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        string hex = text;
        ChecksumAlgorithm? algorithm = null;
        int colon = text.IndexOf(':');
        if (colon > 0)
        {
            algorithm = text[..colon].Trim().Replace("-", "").ToLowerInvariant() switch
            {
                "md5" => ChecksumAlgorithm.Md5,
                "sha1" => ChecksumAlgorithm.Sha1,
                "sha256" => ChecksumAlgorithm.Sha256,
                _ => null,
            };
            if (algorithm is null) return null;
            hex = text[(colon + 1)..].Trim();
        }
        if (hex.Length == 0 || !IsHex(hex)) return null;
        algorithm ??= hex.Length switch
        {
            32 => ChecksumAlgorithm.Md5,
            40 => ChecksumAlgorithm.Sha1,
            64 => ChecksumAlgorithm.Sha256,
            _ => null,
        };
        int expected = algorithm switch { ChecksumAlgorithm.Md5 => 32, ChecksumAlgorithm.Sha1 => 40, _ => 64 };
        return algorithm is { } a && hex.Length == expected ? new ChecksumSpec(a, hex.ToLowerInvariant()) : null;
    }

    /// <summary>Einheitliche Schreibweise zum Speichern, etwa <c>sha256:ab12…</c>.</summary>
    public static string ToText(ChecksumSpec spec) => spec.Algorithm switch
    {
        ChecksumAlgorithm.Md5 => "md5:",
        ChecksumAlgorithm.Sha1 => "sha1:",
        _ => "sha256:",
    } + spec.Hex;

    private static bool IsHex(string s)
    {
        foreach (char c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
