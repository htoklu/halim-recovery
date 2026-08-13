using System.Collections;
using HalimRecovery.Core.IO;
using HalimRecovery.Core.Logging;
using HalimRecovery.Core.Models;

namespace HalimRecovery.Core.FileSystems.Ntfs;

/// <summary>
/// NTFS Quick Scan: streams the MFT, finds FILE records whose in-use flag is cleared
/// (deleted files), reconstructs their original paths from parent references and
/// measures how many of their clusters have since been reallocated ($Bitmap).
/// The volume is only ever read.
/// </summary>
public sealed class NtfsScanner
{
    private const long RootRecordNumber = 5;

    private readonly RawDiskReader _reader;
    private readonly NtfsBootSector _boot;
    private BitArray? _clusterBitmap;

    public NtfsBootSector Boot => _boot;

    public NtfsScanner(RawDiskReader reader)
    {
        _reader = reader;
        _boot = NtfsBootSector.Parse(reader.ReadExact(0, 512));
    }

    public async Task<List<RecoverableFile>> ScanDeletedFilesAsync(
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        return await Task.Run(() => ScanDeletedFiles(progress, ct), ct).ConfigureAwait(false);
    }

    private List<RecoverableFile> ScanDeletedFiles(IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var mftExtents = ReadMftExtents();
        long mftBytes = mftExtents.Sum(e => e.Length);
        long recordCount = mftBytes / _boot.BytesPerMftRecord;
        Log.Info("NtfsScanner", $"MFT: {mftExtents.Count} extent(s), {mftBytes / 1024 / 1024} MiB, ~{recordCount} records");

        LoadClusterBitmap();

        // Pass 1: parse every record; collect name/parent map for path reconstruction.
        var names = new Dictionary<long, (string Name, long Parent)>();
        var deleted = new List<MftRecord>();

        long recordIndex = 0;
        long bytesDone = 0;
        int recSize = _boot.BytesPerMftRecord;
        var chunk = new byte[Math.Max(recSize, 1 << 20) / recSize * recSize]; // ~1 MiB, record-aligned

        foreach (var extent in mftExtents)
        {
            long pos = 0;
            while (pos < extent.Length)
            {
                ct.ThrowIfCancellationRequested();
                int toRead = (int)Math.Min(chunk.Length, extent.Length - pos);
                int got = _reader.ReadAt(extent.Offset + pos, chunk, 0, toRead);
                if (got < recSize) break;

                for (int off = 0; off + recSize <= got; off += recSize, recordIndex++)
                {
                    var recBuf = new byte[recSize];
                    Array.Copy(chunk, off, recBuf, 0, recSize);
                    var rec = MftRecord.Parse(recBuf, recordIndex, _boot.BytesPerSector);
                    if (rec == null || !rec.IsValid) continue;

                    if (rec.FileName.Length > 0 && rec.BaseRecordNumber == 0)
                        names[rec.RecordNumber] = (rec.FileName, rec.ParentRecordNumber);

                    if (!rec.InUse && !rec.IsDirectory && rec.BaseRecordNumber == 0 &&
                        rec.FileName.Length > 0 && rec.HasData)
                        deleted.Add(rec);
                }

                pos += got;
                bytesDone += got;
                progress?.Report(new ScanProgress
                {
                    Phase = "Analyzing MFT",
                    BytesProcessed = bytesDone,
                    BytesTotal = mftBytes,
                    FilesFound = deleted.Count,
                    Elapsed = started.Elapsed
                });
            }
        }

        // Pass 2: build RecoverableFile entries with reconstructed paths + overwrite analysis.
        var results = new List<RecoverableFile>(deleted.Count);
        foreach (var rec in deleted)
        {
            ct.ThrowIfCancellationRequested();
            var file = new RecoverableFile
            {
                Id = rec.RecordNumber,
                FileName = rec.FileName,
                OriginalPath = BuildPath(rec.ParentRecordNumber, names),
                Size = rec.DataSize,
                CreatedUtc = rec.CreatedUtc,
                ModifiedUtc = rec.ModifiedUtc,
                Source = FileSource.FileSystemMetadata,
                ResidentData = rec.ResidentData,
            };
            file.Category = RecoverableFile.CategoryFromExtension(file.Extension);

            long bpc = _boot.BytesPerCluster;
            foreach (var run in rec.DataRuns)
            {
                if (run.Lcn < 0) continue; // sparse
                file.Extents.Add(new FileExtent(run.Lcn * bpc, run.ClusterCount * bpc));
            }
            file.OverwrittenFraction = MeasureOverwrittenFraction(rec.DataRuns);
            results.Add(file);
        }

        Log.Info("NtfsScanner", $"Found {results.Count} deleted file records in {started.Elapsed.TotalSeconds:F1}s");
        return results;
    }

    /// <summary>Reads $MFT's own data runs (record 0) to locate the complete MFT.</summary>
    private List<FileExtent> ReadMftExtents()
    {
        long bpc = _boot.BytesPerCluster;
        var rec0Buf = _reader.ReadExact(_boot.MftStartCluster * bpc, _boot.BytesPerMftRecord);
        var rec0 = MftRecord.Parse(rec0Buf, 0, _boot.BytesPerSector)
            ?? throw new InvalidDataException("MFT record 0 is not a valid FILE record (corrupted filesystem?).");

        var extents = new List<FileExtent>();
        foreach (var run in rec0.DataRuns)
            if (run.Lcn >= 0)
                extents.Add(new FileExtent(run.Lcn * bpc, run.ClusterCount * bpc));

        if (extents.Count == 0)
            throw new InvalidDataException("Could not read $MFT data runs.");
        return extents;
    }

    /// <summary>Loads $Bitmap (record 6): one bit per cluster, 1 = allocated.</summary>
    private void LoadClusterBitmap()
    {
        try
        {
            long bpc = _boot.BytesPerCluster;
            // $Bitmap is MFT record 6; it lives in the first MFT extent.
            var buf = _reader.ReadExact(_boot.MftStartCluster * bpc + 6L * _boot.BytesPerMftRecord, _boot.BytesPerMftRecord);
            var rec = MftRecord.Parse(buf, 6, _boot.BytesPerSector);
            if (rec == null || !rec.HasData) return;

            long bitmapBytes = Math.Min(rec.DataSize, (_boot.TotalClusters + 7) / 8);
            if (bitmapBytes <= 0 || bitmapBytes > 512L * 1024 * 1024) return; // sanity bound

            var bitmapData = new byte[bitmapBytes];
            long written = 0;
            foreach (var run in rec.DataRuns)
            {
                if (run.Lcn < 0 || written >= bitmapBytes) break;
                int len = (int)Math.Min(run.ClusterCount * bpc, bitmapBytes - written);
                _reader.ReadAt(run.Lcn * bpc, bitmapData, (int)written, len);
                written += len;
            }
            _clusterBitmap = new BitArray(bitmapData);
            Log.Info("NtfsScanner", $"$Bitmap loaded ({bitmapBytes / 1024} KiB)");
        }
        catch (Exception ex)
        {
            Log.Warn("NtfsScanner", $"$Bitmap unavailable, overwrite analysis disabled: {ex.Message}");
        }
    }

    /// <summary>
    /// Fraction of a deleted file's clusters that are currently marked allocated,
    /// i.e. reused by other data (its content there is gone). -1 when unknown.
    /// </summary>
    private double MeasureOverwrittenFraction(List<DataRun> runs)
    {
        if (_clusterBitmap == null) return -1;
        long total = 0, reused = 0;
        foreach (var run in runs)
        {
            if (run.Lcn < 0) continue;
            for (long c = run.Lcn; c < run.Lcn + run.ClusterCount; c++)
            {
                if (c >= _clusterBitmap.Length) break;
                total++;
                if (_clusterBitmap[(int)c]) reused++;
            }
        }
        return total == 0 ? -1 : (double)reused / total;
    }

    private static string BuildPath(long parentRecord, Dictionary<long, (string Name, long Parent)> names)
    {
        var parts = new List<string>();
        long current = parentRecord;
        int guard = 0;
        while (current != RootRecordNumber && current > 0 && guard++ < 64)
        {
            if (!names.TryGetValue(current, out var entry)) return @"?\" + string.Join('\\', parts);
            parts.Insert(0, entry.Name);
            if (entry.Parent == current) break; // self-reference guard
            current = entry.Parent;
        }
        return parts.Count == 0 ? @"\" : @"\" + string.Join('\\', parts);
    }
}
