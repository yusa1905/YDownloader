using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace YDownloader;

/// <summary>Liest Links aus Drag &amp; Drop: Text und Links aus dem Browser, Textdateien und Internet-Verknüpfungen (.url).</summary>
public static class DropData
{
    private const string UrlFormat = "UniformResourceLocatorW";
    private const long MaxFileSize = 4 * 1024 * 1024;

    public static bool CanRead(IDataObject data) =>
        data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text)
        || data.GetDataPresent(UrlFormat) || data.GetDataPresent(DataFormats.FileDrop);

    public static string? Read(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
                return ReadFiles(files);
            if (data.GetData(DataFormats.UnicodeText) is string unicode) return unicode;
            if (data.GetData(UrlFormat) is MemoryStream url) return Encoding.Unicode.GetString(url.ToArray()).TrimEnd('\0');
            return data.GetData(DataFormats.Text) as string;
        }
        catch (COMException)
        {
            return null; // Quelle liefert das Format doch nicht
        }
    }

    /// <summary>Kleine Textdateien ganz, aus .url-Dateien nur die Zeile <c>URL=</c>.</summary>
    private static string ReadFiles(string[] files)
    {
        var sb = new StringBuilder();
        foreach (string file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > MaxFileSize) continue;
                if (info.Extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string line in File.ReadLines(file))
                        if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) sb.AppendLine(line[4..]);
                }
                else
                {
                    sb.AppendLine(File.ReadAllText(file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return sb.ToString();
    }
}
