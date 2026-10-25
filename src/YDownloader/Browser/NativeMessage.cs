using System;
using System.Buffers.Binary;
using System.IO;

namespace YDownloader.Browser;

/// <summary>
/// Rahmen der Native-Messaging-Nachrichten: 4 Byte Länge (Little Endian), danach UTF-8-JSON.
/// Chrome und Firefox benutzen dasselbe Format über stdin und stdout.
/// </summary>
public static class NativeMessage
{
    /// <summary>Größte Nachricht, die der Host annimmt. Mehr braucht ein Link-Text nie.</summary>
    public const int MaxIncoming = 1024 * 1024;

    /// <summary>Liest eine Nachricht. <c>null</c>, wenn der Browser die Leitung geschlossen hat (sauberes Ende).</summary>
    /// <exception cref="InvalidDataException">Die Länge ist zu groß oder die Nachricht bricht ab.</exception>
    public static byte[]? Read(Stream input, int max = MaxIncoming)
    {
        Span<byte> head = stackalloc byte[4];
        int got = ReadFully(input, head);
        if (got == 0) return null;
        if (got < 4) throw new InvalidDataException("Nachrichtenkopf unvollständig.");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(head);
        if (length > max) throw new InvalidDataException($"Nachricht zu groß: {length} Byte.");
        var body = new byte[length];
        if (ReadFully(input, body) < body.Length) throw new InvalidDataException("Nachricht unvollständig.");
        return body;
    }

    public static void Write(Stream output, byte[] payload)
    {
        // Eine Nachricht in einem Schreibvorgang, damit Kopf und Inhalt nicht auseinandergerissen werden.
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        output.Write(frame);
        output.Flush();
    }

    private static int ReadFully(Stream s, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
