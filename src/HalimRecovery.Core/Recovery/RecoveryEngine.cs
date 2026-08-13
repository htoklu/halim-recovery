using System.Security.Cryptography;
using HalimRecovery.Core.Disks;
using HalimRecovery.Core.IO;
using HalimRecovery.Core.Logging;
using HalimRecovery.Core.Models;

namespace HalimRecovery.Core.Recovery;

public enum RecoveryStatus { Recovered, PartiallyRecovered, Failed, Skipped }

public sealed record RecoveredItem(RecoverableFile File, RecoveryStatus Status,
    string? OutputPath, long BytesWritten, string? Error, string? Sha256);

/// <summary>
/// Writes recoverable files to a destination folder. The source volume stays read-only;
/// the destination must not be the source volume, and living on the same physical disk
/// triggers a warning the caller must acknowledge.
/// </summary>
public sealed class RecoveryEngine(RawDiskReader sourceReader, string sourceDriveLetter)
{
    public string SourceDriveLetter { get; } = sourceDriveLetter;

    /// <summary>Pre-flight destination check. Returns (isBlocked, warning).</summary>
    public static (bool Blocked, string? Warning) CheckDestination(string sourceDriveLetter, string destinationFolder)
    {
        string? destRoot = Path.GetPathRoot(Path.GetFullPath(destinationFolder))?.TrimEnd('\\', ':');
        if (string.IsNullOrEmpty(destRoot)) return (true, "Destination folder is invalid.");

        if (string.Equals(destRoot, sourceDriveLetter, StringComparison.OrdinalIgnoreCase))
            return (true, "Destination is on the SOURCE volume. Writing there can overwrite the very data you are trying to recover. Choose another drive.");

        int srcDisk = DiskEnumerator.GetPhysicalDiskIndex(sourceDriveLetter);
        int dstDisk = DiskEnumerator.GetPhysicalDiskIndex(destRoot);
        if (srcDisk >= 0 && srcDisk == dstDisk)
            return (false, "Source and destination are on the same physical disk. Recover files to another drive whenever possible.");
        return (false, null);
    }

    public async Task<List<RecoveredItem>> RecoverAsync(IEnumerable<RecoverableFile> files,
        string destinationFolder, bool preserveFolderStructure,
        IProgress<ScanProgress>? progress, CancellationToken ct)
        => await Task.Run(() => Recover(files, destinationFolder, preserveFolderStructure, progress, ct), ct)
            .ConfigureAwait(false);

    private List<RecoveredItem> Recover(IEnumerable<RecoverableFile> files, string destinationFolder,
        bool preserveFolderStructure, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var list = files.ToList();
        var results = new List<RecoveredItem>(list.Count);
        long totalBytes = list.Sum(f => Math.Max(0, f.Size));
        long doneBytes = 0;

        Directory.CreateDirectory(destinationFolder);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destinationFolder))!);
        if (drive.AvailableFreeSpace < totalBytes)
            throw new IOException($"Insufficient space on destination: need {totalBytes / (1 << 20)} MiB, have {drive.AvailableFreeSpace / (1 << 20)} MiB.");

        foreach (var file in list)
        {
            ct.ThrowIfCancellationRequested();
            RecoveredItem item;
            try
            {
                item = RecoverOne(file, destinationFolder, preserveFolderStructure, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error("RecoveryEngine", $"Failed to recover '{file.FileName}'", ex);
                item = new RecoveredItem(file, RecoveryStatus.Failed, null, 0, ex.Message, null);
            }
            results.Add(item);
            doneBytes += Math.Max(0, file.Size);
            progress?.Report(new ScanProgress
            {
                Phase = $"Recovering {file.FileName}",
                BytesProcessed = doneBytes,
                BytesTotal = totalBytes,
                FilesFound = results.Count,
                Elapsed = started.Elapsed
            });
        }
        Log.Info("RecoveryEngine", $"Recovered {results.Count(r => r.Status == RecoveryStatus.Recovered)}/{list.Count} files");
        return results;
    }

    private RecoveredItem RecoverOne(RecoverableFile file, string destRoot, bool preserveStructure, CancellationToken ct)
    {
        string dir = destRoot;
        if (preserveStructure && file.OriginalPath.Length > 1)
        {
            string rel = PathSafety.SanitizeRelativePath(file.OriginalPath);
            if (rel.Length > 0) dir = Path.Combine(destRoot, rel);
        }
        string outPath = Path.Combine(dir, PathSafety.SanitizeFileName(file.FileName));
        if (!PathSafety.IsInsideRoot(destRoot, outPath))
            return new RecoveredItem(file, RecoveryStatus.Failed, null, 0, "Unsafe output path rejected.", null);

        Directory.CreateDirectory(dir);
        outPath = MakeUnique(outPath);

        var source = new ExtentByteSource(file, sourceReader);
        long expected = file.Size > 0 ? Math.Min(file.Size, Math.Max(source.Length, file.Size)) : source.Length;
        long written = 0;
        using (var sha = SHA256.Create())
        using (var stream = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buf = new byte[4 << 20];
            long target = file.Size > 0 ? file.Size : source.Length;
            while (written < target)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buf.Length, target - written);
                int got = source.ReadAt(written, buf, 0, want);
                if (got <= 0) break;
                stream.Write(buf, 0, got);
                sha.TransformBlock(buf, 0, got, null, 0);
                written += got;
            }
            sha.TransformFinalBlock([], 0, 0);
            var status = written == 0 ? RecoveryStatus.Failed
                : written >= target ? RecoveryStatus.Recovered
                : RecoveryStatus.PartiallyRecovered;
            string hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (status == RecoveryStatus.Failed)
            {
                stream.Close();
                File.Delete(outPath);
                return new RecoveredItem(file, status, null, 0, "No data could be read from the source extents.", null);
            }
            return new RecoveredItem(file, status, outPath, written,
                status == RecoveryStatus.PartiallyRecovered ? $"Only {written}/{target} bytes were readable." : null, hash);
        }
    }

    private static string MakeUnique(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 1; i < 10000; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem}_{Guid.NewGuid():N}{ext}");
    }
}
