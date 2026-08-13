using System.Buffers.Binary;
using System.Text;
using HalimRecovery.Core.IO;
using HalimRecovery.Core.Logging;
using HalimRecovery.Core.Models;

namespace HalimRecovery.Core.FileSystems.Fat32;

/// <summary>
/// FAT32 Quick Scan: walks the directory tree (following the FAT for directory clusters)
/// and collects deleted entries (first name byte 0xE5), assembling long file names from
/// preceding LFN entries.
///
/// Honest limitation: FAT32 zeroes a file's FAT chain on delete, so only the first
/// cluster is known. Recovery assumes the file was stored contiguously — true for most
/// files on lightly fragmented volumes, impossible to guarantee otherwise.
/// </summary>
public sealed class Fat32Scanner
{
    private readonly RawDiskReader _reader;
    public int BytesPerSector { get; }
    public int SectorsPerCluster { get; }
    public long FatOffset { get; }
    public long FatSizeBytes { get; }
    public long DataOffset { get; }
    public long RootCluster { get; }
    public long TotalClusters { get; }
    public int BytesPerCluster => BytesPerSector * SectorsPerCluster;

    private uint[]? _fat;

    public Fat32Scanner(RawDiskReader reader)
    {
        _reader = reader;
        var bs = reader.ReadExact(0, 512);
        var s = bs.AsSpan();
        BytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(11, 2));
        SectorsPerCluster = bs[13];
        int reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(14, 2));
        int numFats = bs[16];
        long fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(36, 4));
        RootCluster = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(44, 4));
        long totalSectors = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(32, 4));
        if (totalSectors == 0) totalSectors = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(19, 2));

        if (BytesPerSector is < 256 or > 8192 || SectorsPerCluster == 0 || numFats == 0)
            throw new InvalidDataException("FAT32 boot sector contains implausible values.");

        FatOffset = (long)reservedSectors * BytesPerSector;
        FatSizeBytes = fatSectors * BytesPerSector;
        DataOffset = FatOffset + numFats * FatSizeBytes;
        TotalClusters = (totalSectors * BytesPerSector - DataOffset) / BytesPerCluster;
    }

    public long ClusterToOffset(long cluster) => DataOffset + (cluster - 2) * BytesPerCluster;

    public async Task<List<RecoverableFile>> ScanDeletedFilesAsync(IProgress<ScanProgress>? progress, CancellationToken ct)
        => await Task.Run(() => ScanDeletedFiles(progress, ct), ct).ConfigureAwait(false);

    private List<RecoverableFile> ScanDeletedFiles(IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        LoadFat();
        var results = new List<RecoverableFile>();
        var visited = new HashSet<long>();
        WalkDirectory(RootCluster, "", results, visited, progress, started, ct, depth: 0);
        Log.Info("Fat32Scanner", $"Found {results.Count} deleted entries in {started.Elapsed.TotalSeconds:F1}s");
        return results;
    }

    private void LoadFat()
    {
        long entries = Math.Min(TotalClusters + 2, FatSizeBytes / 4);
        if (entries > 128 * 1024 * 1024) throw new InvalidDataException("FAT too large.");
        var raw = new byte[entries * 4];
        _reader.ReadAt(FatOffset, raw, 0, raw.Length);
        _fat = new uint[entries];
        for (long i = 0; i < entries; i++)
            _fat[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((int)(i * 4), 4)) & 0x0FFFFFFF;
    }

    public bool IsClusterFree(long cluster)
        => _fat != null && cluster >= 2 && cluster < _fat.Length && _fat[cluster] == 0;

    private void WalkDirectory(long startCluster, string path, List<RecoverableFile> results,
        HashSet<long> visited, IProgress<ScanProgress>? progress,
        System.Diagnostics.Stopwatch started, CancellationToken ct, int depth)
    {
        if (depth > 64 || _fat == null) return;

        long cluster = startCluster;
        var lfnParts = new List<(int Seq, string Text)>();
        int chainGuard = 0;

        while (cluster is >= 2 and < 0x0FFFFFF8 && chainGuard++ < 1_000_000)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(cluster)) return; // cycle guard
            if (cluster >= _fat.Length) return;

            var data = new byte[BytesPerCluster];
            if (_reader.ReadAt(ClusterToOffset(cluster), data, 0, data.Length) < data.Length) return;

            for (int off = 0; off + 32 <= data.Length; off += 32)
            {
                var e = data.AsSpan(off, 32);
                byte first = e[0];
                if (first == 0x00) { lfnParts.Clear(); goto nextCluster; } // end of directory
                byte attr = e[11];

                if (attr == 0x0F) // LFN entry (also kept for deleted files, first byte may be 0xE5)
                {
                    int seq = first == 0xE5 ? -1 : first & 0x1F;
                    lfnParts.Add((seq, ReadLfnChars(e)));
                    continue;
                }

                bool deleted = first == 0xE5;
                bool isDir = (attr & 0x10) != 0;
                bool isVolumeLabel = (attr & 0x08) != 0;
                string longName = AssembleLfn(lfnParts);
                lfnParts.Clear();
                if (isVolumeLabel) continue;

                long entryCluster = ((long)BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(20, 2)) << 16)
                                    | BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(26, 2));
                string shortName = ReadShortName(e, deleted);
                string name = longName.Length > 0 ? longName : shortName;
                if (name is "." or ".." || name.Length == 0) continue;

                if (!deleted && isDir)
                {
                    // Invariant: a genuinely live directory always has its first cluster
                    // allocated in the FAT. A "live-looking" entry whose cluster is free is
                    // an orphaned directory (deleted tree whose entry update never hit disk).
                    if (entryCluster >= 2 && entryCluster < TotalClusters + 2 && IsClusterFree(entryCluster))
                        WalkDeletedDirectory(entryCluster, path + "\\" + name, results, visited, progress, started, ct, depth + 1);
                    else
                        WalkDirectory(entryCluster, path + "\\" + name, results, visited, progress, started, ct, depth + 1);
                }
                else if (deleted && !isDir)
                {
                    long size = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(28, 4));
                    if (size == 0 || entryCluster < 2 || entryCluster >= TotalClusters + 2) continue;

                    var file = new RecoverableFile
                    {
                        Id = results.Count + 1,
                        FileName = name,
                        OriginalPath = path.Length == 0 ? "\\" : path,
                        Size = size,
                        CreatedUtc = FatDateTime(e.Slice(16, 2), e.Slice(14, 2)),
                        ModifiedUtc = FatDateTime(e.Slice(24, 2), e.Slice(22, 2)),
                        Source = FileSource.FileSystemMetadata,
                    };
                    file.Category = RecoverableFile.CategoryFromExtension(file.Extension);

                    // FAT chain is zeroed on delete: assume contiguous storage from the start cluster.
                    file.Extents.Add(new FileExtent(ClusterToOffset(entryCluster), size));
                    file.HealthNotes.Add("FAT32: cluster chain lost on delete; contiguous layout assumed");
                    file.OverwrittenFraction = MeasureReusedFraction(entryCluster, size);
                    results.Add(file);

                    progress?.Report(new ScanProgress
                    {
                        Phase = "Scanning directories",
                        BytesProcessed = results.Count,
                        BytesTotal = 0,
                        FilesFound = results.Count,
                        Elapsed = started.Elapsed
                    });
                }
                else if (deleted && isDir && entryCluster >= 2 && entryCluster < TotalClusters + 2
                         && IsClusterFree(entryCluster))
                {
                    // Deleted directory: its first cluster may still hold the directory table.
                    WalkDeletedDirectory(entryCluster, path + "\\" + name, results, visited, progress, started, ct, depth + 1);
                }
            }
            nextCluster:
            cluster = _fat[cluster];
        }
    }

    /// <summary>
    /// Harvests every entry of an orphaned (deleted/unreachable) directory. All children are
    /// recoverable regardless of their own deleted flag, because Windows does not rewrite
    /// child entries when it deletes a whole tree. Walks contiguous clusters until the
    /// directory terminator (the chain was cleared in the FAT).
    /// </summary>
    private void WalkDeletedDirectory(long startCluster, string path, List<RecoverableFile> results,
        HashSet<long> visited, IProgress<ScanProgress>? progress,
        System.Diagnostics.Stopwatch started, CancellationToken ct, int depth)
    {
        if (depth > 64) return;
        for (long cluster = startCluster; cluster < startCluster + 256 && cluster < TotalClusters + 2; cluster++)
        {
            if (!visited.Add(cluster)) return;
            var data = new byte[BytesPerCluster];
            if (_reader.ReadAt(ClusterToOffset(cluster), data, 0, data.Length) < data.Length) return;
            // First cluster must look like a directory ("." entry or a deleted entry first).
            if (cluster == startCluster && data[0] != '.' && data[0] != 0xE5) return;
            if (cluster != startCluster && !IsClusterFree(cluster)) return; // reused by live data

            bool terminated = HarvestDirectoryCluster(data, path, results, visited, progress, started, ct, depth);
            if (terminated) return;
        }
    }

    /// <summary>Processes one directory cluster; returns true when the 0x00 terminator was seen.</summary>
    private bool HarvestDirectoryCluster(byte[] data, string path, List<RecoverableFile> results,
        HashSet<long> visited, IProgress<ScanProgress>? progress,
        System.Diagnostics.Stopwatch started, CancellationToken ct, int depth)
    {
        var lfnParts = new List<(int, string)>();
        for (int off = 0; off + 32 <= data.Length; off += 32)
        {
            ct.ThrowIfCancellationRequested();
            var e = data.AsSpan(off, 32);
            byte first = e[0];
            if (first == 0x00) return true;
            byte attr = e[11];
            if (attr == 0x0F) { lfnParts.Add((first == 0xE5 ? -1 : first & 0x1F, ReadLfnChars(e))); continue; }

            string longName = AssembleLfn(lfnParts);
            lfnParts.Clear();
            if ((attr & 0x08) != 0) continue;
            string name = longName.Length > 0 ? longName : ReadShortName(e, first == 0xE5);
            if (name is "." or ".." || name.Length == 0) continue;
            bool isDir = (attr & 0x10) != 0;
            long entryCluster = ((long)BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(20, 2)) << 16)
                                | BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(26, 2));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(28, 4));
            if (isDir)
            {
                if (entryCluster >= 2 && entryCluster < TotalClusters + 2 && IsClusterFree(entryCluster))
                    WalkDeletedDirectory(entryCluster, path + "\\" + name, results, visited, progress, started, ct, depth + 1);
                continue;
            }
            if (size == 0 || entryCluster < 2 || entryCluster >= TotalClusters + 2) continue;

            var file = new RecoverableFile
            {
                Id = results.Count + 1,
                FileName = name,
                OriginalPath = path,
                Size = size,
                CreatedUtc = FatDateTime(e.Slice(16, 2), e.Slice(14, 2)),
                ModifiedUtc = FatDateTime(e.Slice(24, 2), e.Slice(22, 2)),
                Source = FileSource.FileSystemMetadata,
            };
            file.Category = RecoverableFile.CategoryFromExtension(file.Extension);
            file.Extents.Add(new FileExtent(ClusterToOffset(entryCluster), size));
            file.HealthNotes.Add("FAT32: recovered from deleted directory; contiguous layout assumed");
            file.OverwrittenFraction = MeasureReusedFraction(entryCluster, size);
            results.Add(file);
        }
        return false; // no terminator in this cluster: directory continues
    }

    private double MeasureReusedFraction(long startCluster, long size)
    {
        if (_fat == null) return -1;
        long clusters = (size + BytesPerCluster - 1) / BytesPerCluster;
        long total = 0, reused = 0;
        for (long c = startCluster; c < startCluster + clusters && c < _fat.Length; c++)
        {
            total++;
            if (_fat[c] != 0) reused++; // non-zero FAT entry = cluster belongs to a live file now
        }
        return total == 0 ? -1 : (double)reused / total;
    }

    private static string ReadLfnChars(ReadOnlySpan<byte> e)
    {
        Span<byte> chars = stackalloc byte[26];
        e.Slice(1, 10).CopyTo(chars);
        e.Slice(14, 12).CopyTo(chars.Slice(10));
        e.Slice(28, 4).CopyTo(chars.Slice(22));
        string s = Encoding.Unicode.GetString(chars);
        int end = s.IndexOf('\0');
        return end >= 0 ? s[..end] : s;
    }

    /// <summary>LFN entries appear in reverse order on disk; for deleted files sequence bytes are lost, so keep disk order reversed.</summary>
    private static string AssembleLfn(List<(int Seq, string Text)> parts)
    {
        if (parts.Count == 0) return "";
        var ordered = parts.All(p => p.Seq > 0)
            ? parts.OrderBy(p => p.Seq).Select(p => p.Text)
            : Enumerable.Reverse(parts).Select(p => p.Text);
        return string.Concat(ordered).Trim();
    }

    private static string ReadShortName(ReadOnlySpan<byte> e, bool deleted)
    {
        Span<char> name = stackalloc char[8];
        for (int i = 0; i < 8; i++) name[i] = (char)e[i];
        if (deleted) name[0] = '_'; // first byte destroyed by 0xE5 marker
        string baseName = new string(name).TrimEnd(' ');
        string ext = Encoding.ASCII.GetString(e.Slice(8, 3)).TrimEnd(' ');
        return ext.Length > 0 ? $"{baseName}.{ext}" : baseName;
    }

    private static DateTime? FatDateTime(ReadOnlySpan<byte> dateBytes, ReadOnlySpan<byte> timeBytes)
    {
        int date = BinaryPrimitives.ReadUInt16LittleEndian(dateBytes);
        int time = BinaryPrimitives.ReadUInt16LittleEndian(timeBytes);
        if (date == 0) return null;
        int year = 1980 + (date >> 9), month = (date >> 5) & 0xF, day = date & 0x1F;
        int hour = time >> 11, min = (time >> 5) & 0x3F, sec = (time & 0x1F) * 2;
        try { return new DateTime(year, month, day, hour, Math.Min(min, 59), Math.Min(sec, 59), DateTimeKind.Local).ToUniversalTime(); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
