using System.IO.Compression;
using System.Text;
using HalimRecovery.Core.IO;
using HalimRecovery.Core.Models;

namespace HalimRecovery.Core.Preview;

public enum PreviewKind { Image, Text, Metadata, None }

public sealed record PreviewResult(PreviewKind Kind, byte[]? ImageBytes, string? Text, string? FailureReason);

/// <summary>
/// Best-effort preview of a recoverable file *before* recovery, reading only from the
/// (read-only) source. Corrupted data yields a partial preview or a clear failure reason.
/// </summary>
public static class PreviewProvider
{
    private const int MaxPreviewBytes = 32 << 20;

    public static PreviewResult GetPreview(RecoverableFile file, RawDiskReader reader)
    {
        try
        {
            var source = new ExtentByteSource(file, reader);
            if (source.Length == 0) return new(PreviewKind.None, null, null, "No data available on disk for this file.");

            return file.Extension switch
            {
                "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" => ImagePreview(file, source),
                "txt" or "log" or "csv" or "md" or "xml" or "json" or "ini" => TextPreview(source),
                "pdf" => PdfTextPreview(source),
                "docx" => DocxPreview(source),
                "xlsx" => XlsxPreview(source),
                "mp3" or "mp4" or "mov" or "wav" or "m4a" => MetadataPreview(file),
                "zip" or "pptx" => ZipListPreview(source),
                _ => new(PreviewKind.Metadata, null, DescribeFile(file), null)
            };
        }
        catch (Exception ex)
        {
            return new(PreviewKind.None, null, null, $"Preview failed: {ex.Message}");
        }
    }

    private static PreviewResult ImagePreview(RecoverableFile file, IByteSource source)
    {
        long size = Math.Min(source.Length, MaxPreviewBytes);
        var bytes = new byte[size];
        int got = source.ReadAt(0, bytes, 0, (int)size);
        if (got <= 0) return new(PreviewKind.None, null, null, "Image data is unreadable.");
        if (got < size) Array.Resize(ref bytes, got);
        // Decoding is done by the UI layer (WPF BitmapImage); a partially overwritten
        // image may still render its upper portion.
        return new(PreviewKind.Image, bytes, null, null);
    }

    private static PreviewResult TextPreview(IByteSource source)
    {
        var bytes = new byte[Math.Min(source.Length, 64 * 1024)];
        int got = source.ReadAt(0, bytes, 0, bytes.Length);
        if (got <= 0) return new(PreviewKind.None, null, null, "Text data is unreadable.");
        string text = Encoding.UTF8.GetString(bytes, 0, got).Replace("\0", "");
        return new(PreviewKind.Text, null, text, null);
    }

    private static PreviewResult PdfTextPreview(IByteSource source)
    {
        // Show PDF header info + embedded readable strings from the first chunk.
        var bytes = new byte[Math.Min(source.Length, 256 * 1024)];
        int got = source.ReadAt(0, bytes, 0, bytes.Length);
        if (got < 8) return new(PreviewKind.None, null, null, "PDF data is unreadable.");
        string header = Encoding.ASCII.GetString(bytes, 0, Math.Min(16, got));
        if (!header.StartsWith("%PDF-"))
            return new(PreviewKind.None, null, null, "Data no longer looks like a PDF (content may be overwritten).");
        var sb = new StringBuilder();
        sb.AppendLine($"PDF version: {header.TrimEnd()[5..Math.Min(8, header.TrimEnd().Length)]}");
        int pages = CountOccurrences(bytes, got, "/Type /Page"u8.ToArray()) + CountOccurrences(bytes, got, "/Type/Page"u8.ToArray());
        if (pages > 0) sb.AppendLine($"Page objects found in first 256 KiB: {pages}");
        sb.AppendLine("(Full rendering is available after recovery.)");
        return new(PreviewKind.Text, null, sb.ToString(), null);
    }

    private static PreviewResult DocxPreview(IByteSource source)
    {
        try
        {
            using var archive = OpenZip(source);
            var entry = archive.GetEntry("word/document.xml");
            if (entry == null) return new(PreviewKind.None, null, null, "DOCX structure incomplete: word/document.xml missing.");
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms, 1 << 16);
            string xml = Encoding.UTF8.GetString(ms.ToArray());
            string text = ExtractXmlText(xml, 4000);
            return new(PreviewKind.Text, null, text.Length > 0 ? text : "(Document contains no plain text.)", null);
        }
        catch (InvalidDataException)
        {
            return new(PreviewKind.None, null, null, "DOCX archive is damaged and cannot be opened.");
        }
    }

    private static PreviewResult XlsxPreview(IByteSource source)
    {
        try
        {
            using var archive = OpenZip(source);
            var shared = archive.GetEntry("xl/sharedStrings.xml");
            var sheets = archive.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/")).Select(e => e.Name).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"Worksheets: {(sheets.Count > 0 ? string.Join(", ", sheets) : "none found")}");
            if (shared != null)
            {
                using var s = shared.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms, 1 << 16);
                string text = ExtractXmlText(Encoding.UTF8.GetString(ms.ToArray()), 2000);
                if (text.Length > 0) sb.AppendLine("Cell text sample:").AppendLine(text);
            }
            return new(PreviewKind.Text, null, sb.ToString(), null);
        }
        catch (InvalidDataException)
        {
            return new(PreviewKind.None, null, null, "XLSX archive is damaged and cannot be opened.");
        }
    }

    private static PreviewResult ZipListPreview(IByteSource source)
    {
        try
        {
            using var archive = OpenZip(source);
            var names = archive.Entries.Take(50).Select(e => e.FullName);
            return new(PreviewKind.Text, null, "Archive contents:\n" + string.Join('\n', names), null);
        }
        catch (InvalidDataException)
        {
            return new(PreviewKind.None, null, null, "Archive is damaged and cannot be listed.");
        }
    }

    private static PreviewResult MetadataPreview(RecoverableFile file)
        => new(PreviewKind.Metadata, null, DescribeFile(file), null);

    private static string DescribeFile(RecoverableFile file)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Name: {file.FileName}");
        sb.AppendLine($"Size: {file.Size:N0} bytes");
        if (file.CreatedUtc != null) sb.AppendLine($"Created: {file.CreatedUtc:yyyy-MM-dd HH:mm} UTC");
        if (file.ModifiedUtc != null) sb.AppendLine($"Modified: {file.ModifiedUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"Source: {(file.Source == FileSource.Carved ? "Deep scan (carved)" : "Filesystem metadata")}");
        sb.AppendLine("Media preview is available after recovery.");
        return sb.ToString();
    }

    private static ZipArchive OpenZip(IByteSource source)
    {
        long size = Math.Min(source.Length, MaxPreviewBytes);
        var bytes = new byte[size];
        int got = source.ReadAt(0, bytes, 0, (int)size);
        if (got < size) Array.Resize(ref bytes, Math.Max(0, got));
        return new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
    }

    private static string ExtractXmlText(string xml, int maxLen)
    {
        var sb = new StringBuilder();
        bool inTag = false;
        foreach (char c in xml)
        {
            if (c == '<') { inTag = true; if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); }
            else if (c == '>') inTag = false;
            else if (!inTag) sb.Append(c);
            if (sb.Length >= maxLen) break;
        }
        return sb.ToString().Trim();
    }

    private static int CountOccurrences(byte[] data, int length, byte[] pattern)
    {
        int count = 0;
        for (int i = 0; i <= length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length && match; j++)
                if (data[i + j] != pattern[j]) match = false;
            if (match) count++;
        }
        return count;
    }
}
