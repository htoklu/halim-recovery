using System.Buffers.Binary;
using System.Text;
using HalimRecovery.Core.IO;
using HalimRecovery.Core.Logging;
using HalimRecovery.Core.Models;

namespace HalimRecovery.Core.FileSystems.ExFat;

/// <summary>
/// exFAT Quick Scan: walks directory entry sets. Deleted files keep their full entry set
/// with the in-use bit (0x80) of each entry type cleared (0x85→0x05, 0xC0→0x40, 0xC1→0x41).
/// Files stored without a FAT chain (NoFatChain flag, the common case) are exactly
/// recoverable when their clusters have not been reused.
/// </summary>
public sealed class ExFatScanner
{
    private readonly RawDiskReader _reader;
    public int BytesPerSector { get; }
    public int SectorsPerCluster { get; }
    public long FatOffsetBytes { get; }
    public long ClusterHeapOffsetBytes { get; }
    public long ClusterCount { get; }
    public long RootDirCluster { get; }
    public int BytesPerCluster => BytesPerSector * SectorsPerCluster;

    private uint[]? _fat;

    public ExFatScanner(RawDiskReader reader)
    {
        _reader = reader;
        var bs = reader.ReadExact(0, 512);
        var s = bs.AsSpan();
        if (Encoding.ASCII.GetString(s.Slice(3, 8)) != "EXFAT   ")
            throw new InvalidDataException("Not an exFAT volume.");

        BytesPerSector = 1 << bs[108];
        SectorsPerCluster = 1 << bs[109];
        long fatOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(80, 4));
        long fatLengthSectors = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(84, 4));
        long heapOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(88, 4));
        ClusterCount = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(92, 4));
        RootDirCluster = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(96, 4));

        FatOffsetBytes = fatOffsetSectors * BytesPerSector;
        ClusterHeapOffsetBytes = heapOffsetSectors * BytesPerSector;
        FatLengthBytes = fatLengthSectors * BytesPerSector;

        if (BytesPerSector is < 256 or > 8192 || RootDirCluster < 2)
            throw new InvalidDataException("exFAT boot sector contains implausible values.");
    }

    public long FatLengthBytes { get; }

    public long ClusterToOffset(long cluster) => ClusterHeapOffsetBytes + (cluster - 2) * BytesPerCluster;

    public async Task<List<RecoverableFile>> ScanDeletedFilesAsync(IProgress<ScanProgress>? progress, CancellationToken ct)
        => await Task.Run(() => ScanDeletedFiles(progress, ct), ct).ConfigureAwait(false);

    private List<RecoverableFile> ScanDeletedFiles(IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        LoadFat();
        var results = new List<RecoverableFile>();
        var visited = new HashSet<long>();
        WalkDirectory(RootDirCluster, "", chainValid: true, maxClusters: long.MaxValue,
            treatAllAsDeleted: false, results, visited, progress, started, ct, 0);
        Log.Info("ExFatScanner", $"Found {results.Count} deleted entries in {started.Elapsed.TotalSeconds:F1}s");
        return results;
    }

    /// <summary>Known exFAT directory entry type codes (in-use and deleted variants) plus end-of-directory.</summary>
    private static bool LooksLikeDirectoryCluster(byte firstType) => firstType is
        0x00 or 0x81 or 0x82 or 0x83 or 0x85 or 0xA0 or 0xA1 or 0xA2 or 0xC0 or 0xC1 or
        0x01 or 0x02 or 0x03 or 0x05 or 0x20 or 0x21 or 0x22 or 0x40 or 0x41 or 0x60 or 0x61;

    private void LoadFat()
    {
        try
        {
            long entries = Math.Min(ClusterCount + 2, FatLengthBytes / 4);
            if (entries <= 0 || entries > 128 * 1024 * 1024) return;
            var raw = new byte[entries * 4];
            _reader.ReadAt(FatOffsetBytes, raw, 0, raw.Length);
            _fat = new uint[entries];
            for (long i = 0; i < entries; i++)
                _fat[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((int)(i * 4), 4));
        }
        catch (Exception ex)
        {
            Log.Warn("ExFatScanner", $"FAT unavailable: {ex.Message}");
        }
    }

    /// <param name="treatAllAsDeleted">
    /// True when walking a directory that is itself deleted/unreachable: every file inside
    /// is unreachable from the live tree, so all entries are recoverable — Windows does not
    /// rewrite child entries as "deleted" when it deletes a whole directory tree.
    /// </param>
    private void WalkDirectory(long startCluster, string path, bool chainValid, long maxClusters,
        bool treatAllAsDeleted, List<RecoverableFile> results, HashSet<long> visited,
        IProgress<ScanProgress>? progress, System.Diagnostics.Stopwatch started, CancellationToken ct, int depth)
    {
        if (depth > 64) return;
        long cluster = startCluster;
        long clustersWalked = 0;
        int guard = 0;

        // Pending file entry set state (spans multiple 32-byte entries).
        byte pendingSecondaries = 0; bool pendingDeleted = false;
        DateTime? createdUtc = null, modifiedUtc = null;
        bool haveStream = false, noFatChain = false;
        long firstCluster = 0, dataLength = 0; int nameLength = 0;
        var nameBuilder = new StringBuilder();
        bool pendingIsDir = false;

        while (cluster >= 2 && clustersWalked++ < maxClusters && guard++ < 1_000_000)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(cluster)) break;

            var data = new byte[BytesPerCluster];
            if (_reader.ReadAt(ClusterToOffset(cluster), data, 0, data.Length) < data.Length) break;
            if (!LooksLikeDirectoryCluster(data[0])) break; // strayed into non-directory data

            for (int off = 0; off + 32 <= data.Length; off += 32)
            {
                var e = data.AsSpan(off, 32);
                byte type = e[0];
                if (type == 0x00) goto done; // end of directory

                switch (type)
                {
                    case 0x85 or 0x05: // file directory entry (0x05 = deleted)
                        pendingDeleted = type == 0x05;
                        pendingSecondaries = e[1];
                        pendingIsDir = (BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(4, 2)) & 0x10) != 0;
                        createdUtc = ExFatTimestamp(BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(8, 4)));
                        modifiedUtc = ExFatTimestamp(BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(12, 4)));
                        haveStream = false; nameBuilder.Clear(); nameLength = 0;
                        break;

                    case 0xC0 or 0x40: // stream extension
                        noFatChain = (e[1] & 0x02) != 0;
                        nameLength = e[3];
                        firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(20, 4));
                        dataLength = BinaryPrimitives.ReadInt64LittleEndian(e.Slice(24, 8));
                        haveStream = true;
                        break;

                    case 0xC1 or 0x41: // file name entry (15 UTF-16 chars each)
                        nameBuilder.Append(Encoding.Unicode.GetString(e.Slice(2, 30)));
                        if (nameBuilder.Length >= nameLength && haveStream && nameLength > 0)
                        {
                            string name = nameBuilder.ToString()[..Math.Min(nameLength, nameBuilder.Length)].TrimEnd('\0');
                            EmitEntry(name, path, pendingDeleted || treatAllAsDeleted, pendingIsDir, noFatChain,
                                firstCluster, dataLength, createdUtc, modifiedUtc, treatAllAsDeleted,
                                results, visited, progress, started, ct, depth);
                            nameLength = 0; haveStream = false;
                        }
                        break;
                }
            }

            if (chainValid && _fat != null && cluster < _fat.Length)
            {
                uint next = _fat[cluster];
                if (next is >= 2 and < 0xFFFFFFF7) { cluster = next; continue; }
                break;
            }
            // Directories use FAT chains (NoFatChain rarely set); without FAT assume contiguous.
            cluster++;
            if (cluster >= ClusterCount + 2) break;
        }
        done: ;
    }

    private void EmitEntry(string name, string path, bool deleted, bool isDir, bool noFatChain,
        long firstCluster, long dataLength, DateTime? createdUtc, DateTime? modifiedUtc, bool parentDeleted,
        List<RecoverableFile> results, HashSet<long> visited, IProgress<ScanProgress>? progress,
        System.Diagnostics.Stopwatch started, CancellationToken ct, int depth)
    {
        if (name.Length == 0 || firstCluster < 2 || firstCluster >= ClusterCount + 2) return;

        if (isDir)
        {
            // Recurse into live directories, and into deleted/unreachable ones whose clusters are still free.
            bool clusterFree = _fat == null || firstCluster >= _fat.Length || _fat[firstCluster] == 0;
            if (!deleted || clusterFree)
            {
                long dirClusters = dataLength > 0 ? (dataLength + BytesPerCluster - 1) / BytesPerCluster : 1;
                bool live = !deleted && !parentDeleted;
                WalkDirectory(firstCluster, path + "\\" + name,
                    chainValid: live && !noFatChain,
                    maxClusters: live && !noFatChain ? long.MaxValue : dirClusters,
                    treatAllAsDeleted: deleted || parentDeleted,
                    results, visited, progress, started, ct, depth + 1);
            }
            return;
        }
        if (!deleted || dataLength <= 0) return;

        var file = new RecoverableFile
        {
            Id = results.Count + 1,
            FileName = name,
            OriginalPath = path.Length == 0 ? "\\" : path,
            Size = dataLength,
            CreatedUtc = createdUtc,
            ModifiedUtc = modifiedUtc,
            Source = FileSource.FileSystemMetadata,
        };
        file.Category = RecoverableFile.CategoryFromExtension(file.Extension);
        file.Extents.Add(new FileExtent(ClusterToOffset(firstCluster), dataLength));
        if (noFatChain)
            file.HealthNotes.Add("exFAT: NoFatChain set — file was stored contiguously (exact layout known)");
        else
            file.HealthNotes.Add("exFAT: FAT chain cleared on delete; contiguous layout assumed");
        file.OverwrittenFraction = MeasureReusedFraction(firstCluster, dataLength);
        results.Add(file);

        progress?.Report(new ScanProgress
        {
            Phase = "Scanning directories",
            FilesFound = results.Count,
            Elapsed = started.Elapsed
        });
    }

    private double MeasureReusedFraction(long startCluster, long size)
    {
        if (_fat == null) return -1;
        long clusters = (size + BytesPerCluster - 1) / BytesPerCluster;
        long total = 0, reused = 0;
        for (long c = startCluster; c < startCluster + clusters && c < _fat.Length; c++)
        {
            total++;
            if (_fat[c] != 0) reused++;
        }
        return total == 0 ? -1 : (double)reused / total;
    }

    /// <summary>exFAT timestamp: FAT-packed date/time in one u32 (date high word, time low word).</summary>
    private static DateTime? ExFatTimestamp(uint ts)
    {
        if (ts == 0) return null;
        int date = (int)(ts >> 16), time = (int)(ts & 0xFFFF);
        int year = 1980 + (date >> 9), month = (date >> 5) & 0xF, day = date & 0x1F;
        int hour = time >> 11, min = (time >> 5) & 0x3F, sec = (time & 0x1F) * 2;
        try { return new DateTime(year, month, day, hour, Math.Min(min, 59), Math.Min(sec, 59), DateTimeKind.Local).ToUniversalTime(); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
