using HalimRecovery.Core.Carving;
using HalimRecovery.Core.FileSystems;
using HalimRecovery.Core.FileSystems.ExFat;
using HalimRecovery.Core.FileSystems.Fat32;
using HalimRecovery.Core.FileSystems.Ntfs;
using HalimRecovery.Core.Health;
using HalimRecovery.Core.IO;
using HalimRecovery.Core.Logging;
using HalimRecovery.Core.Models;

namespace HalimRecovery.Core.Scanning;

/// <summary>
/// Top-level orchestration: opens a volume read-only, detects the filesystem,
/// runs Quick Scan (metadata) or Deep Scan (carving) and scores every result.
/// </summary>
public sealed class ScanEngine : IDisposable
{
    private readonly RawDiskReader _reader;
    public string DriveLetter { get; }
    public FileSystemKind FileSystem { get; }
    public PauseToken Pause { get; } = new();

    /// <param name="driveLetterOrImage">A drive letter ("E") or a path to a raw volume image file.</param>
    public ScanEngine(string driveLetterOrImage)
    {
        if (File.Exists(driveLetterOrImage))
        {
            DriveLetter = "";
            _reader = new RawDiskReader(Path.GetFullPath(driveLetterOrImage));
        }
        else
        {
            DriveLetter = driveLetterOrImage.TrimEnd(':', '\\');
            _reader = new RawDiskReader($@"\\.\{DriveLetter}:");
        }
        FileSystem = FileSystemDetector.Detect(_reader);
        Log.Info("ScanEngine", $"Opened {driveLetterOrImage}: filesystem={FileSystem}, size={_reader.Length / (1 << 20)} MiB");
    }

    public RawDiskReader Reader => _reader;

    /// <summary>Quick Scan: filesystem metadata analysis. Fails clearly on unsupported filesystems.</summary>
    public async Task<List<RecoverableFile>> QuickScanAsync(IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var files = FileSystem switch
        {
            FileSystemKind.Ntfs => await new NtfsScanner(_reader).ScanDeletedFilesAsync(progress, ct).ConfigureAwait(false),
            FileSystemKind.Fat32 => await new Fat32Scanner(_reader).ScanDeletedFilesAsync(progress, ct).ConfigureAwait(false),
            FileSystemKind.ExFat => await new ExFatScanner(_reader).ScanDeletedFilesAsync(progress, ct).ConfigureAwait(false),
            _ => throw new NotSupportedException(
                $"Quick Scan supports NTFS, FAT32 and exFAT. This volume is {FileSystem}. Deep Scan (signature search) can still be used.")
        };

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            HealthScorer.Score(f, new ExtentByteSource(f, _reader));
        }
        return files;
    }

    /// <summary>Deep Scan: raw signature carving over the whole volume (any filesystem).</summary>
    public async Task<List<RecoverableFile>> DeepScanAsync(IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var scanner = new DeepScanner(_reader);
        var files = await scanner.ScanAsync(progress, Pause, ct).ConfigureAwait(false);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            HealthScorer.Score(f, new ExtentByteSource(f, _reader));
        }
        return files;
    }

    public void Dispose() => _reader.Dispose();
}
