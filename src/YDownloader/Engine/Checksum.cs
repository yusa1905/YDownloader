using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace YDownloader.Engine;

public enum ChecksumAlgorithm { Md5, Sha1, Sha256 }

/// <summary>Erwartete Prüfsumme als Hex-Text (Groß/Klein egal).</summary>
public sealed record ChecksumSpec(ChecksumAlgorithm Algorithm, string Hex);

/// <summary>Kein <see cref="IOException"/>: Ein neuer Versuch mit denselben Daten ändert nichts.</summary>
public sealed class ChecksumMismatchException : Exception
{
    public ChecksumMismatchException(string expected, string actual)
        : base($"Prüfsumme stimmt nicht: erwartet {expected}, berechnet {actual}.")
    {
        Expected = expected;
        Actual = actual;
    }

    /// <summary>Die Meldung ist Entwicklertext, die Anzeige baut ihren Text aus diesen beiden (siehe <c>ErrorText</c>).</summary>
    public string Expected { get; }

    public string Actual { get; }
}

public static class Checksums
{
    public static async Task<string> ComputeAsync(string path, ChecksumAlgorithm algorithm, CancellationToken ct)
    {
        using HashAlgorithm hash = algorithm switch
        {
            ChecksumAlgorithm.Md5 => MD5.Create(),
            ChecksumAlgorithm.Sha1 => SHA1.Create(),
            _ => SHA256.Create(),
        };
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] bytes = await hash.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static async Task VerifyAsync(string path, ChecksumSpec spec, CancellationToken ct)
    {
        string actual = await ComputeAsync(path, spec.Algorithm, ct).ConfigureAwait(false);
        string expected = spec.Hex.Trim().ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new ChecksumMismatchException(expected, actual);
    }
}
