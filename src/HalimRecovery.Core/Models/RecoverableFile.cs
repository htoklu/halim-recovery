namespace HalimRecovery.Core.Models;

public enum RecoveryHealth
{
    /// <summary>High confidence: structure validated, clusters not reallocated.</summary>
    Green,
    /// <summary>Partial / uncertain: some evidence of damage or reallocation.</summary>
    Yellow,
    /// <summary>Low confidence: clusters overwritten or structure invalid.</summary>
    Red
}

public enum FileCategory { Image, Video, Document, Audio, Archive, Text, Other }

public enum FileSource
{
    /// <summary>Found via filesystem metadata (Quick Scan).</summary>
    FileSystemMetadata,
    /// <summary>Found via raw signature carving (Deep Scan).</summary>
    Carved
}

/// <summary>A contiguous byte range on the source volume.</summary>
public readonly record struct FileExtent(long Offset, long Length);

/// <summary>A deleted file discovered by a scan, not yet recovered.</summary>
public sealed class RecoverableFile
{
    public long Id { get; set; }
    public string FileName { get; set; } = "";
    /// <summary>Original directory path if known (Quick Scan), else "".</summary>
    public string OriginalPath { get; set; } = "";
    public long Size { get; set; }
    public string Extension => Path.GetExtension(FileName).TrimStart('.').ToLowerInvariant();
    public FileCategory Category { get; set; } = FileCategory.Other;
    public DateTime? CreatedUtc { get; set; }
    public DateTime? ModifiedUtc { get; set; }
    public FileSource Source { get; set; }

    /// <summary>Byte ranges on the source volume. Empty if data is resident.</summary>
    public List<FileExtent> Extents { get; } = new();
    /// <summary>For NTFS resident files: the data itself (small files stored in MFT).</summary>
    public byte[]? ResidentData { get; set; }

    public RecoveryHealth Health { get; set; } = RecoveryHealth.Yellow;
    /// <summary>0-100, computed only from measurable evidence.</summary>
    public int Confidence { get; set; }
    /// <summary>Human-readable evidence notes ("signature valid", "clusters reused", ...).</summary>
    public List<string> HealthNotes { get; } = new();

    /// <summary>Fraction of this file's clusters currently marked allocated to other data (0..1). -1 = unknown.</summary>
    public double OverwrittenFraction { get; set; } = -1;
    public bool IsFragmented => Extents.Count > 1;

    public static FileCategory CategoryFromExtension(string ext) => ext.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "tif" or "tiff" or "heic" => FileCategory.Image,
        "mp4" or "mov" or "avi" or "mkv" or "wmv" or "m4v" => FileCategory.Video,
        "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "rtf" or "odt" => FileCategory.Document,
        "mp3" or "wav" or "flac" or "m4a" or "wma" or "ogg" => FileCategory.Audio,
        "zip" or "rar" or "7z" or "tar" or "gz" or "cab" => FileCategory.Archive,
        "txt" or "log" or "csv" or "md" or "xml" or "json" or "ini" => FileCategory.Text,
        _ => FileCategory.Other
    };
}
