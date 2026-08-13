using HalimRecovery.Core.IO;
using HalimRecovery.Core.Logging;
using HalimRecovery.Core.Models;
using HalimRecovery.Core.Scanning;

namespace HalimRecovery.Core.Carving;

/// <summary>
/// Deep Scan: streams the raw volume sector by sector, detects file signatures and
/// carves files by parsing their internal structure (see FormatSpecs). Carved files
/// have no original name/path — those live only in filesystem metadata.
/// Memory is bounded: one scan buffer, measurement reads are windowed.
/// </summary>
public sealed class DeepScanner
{
    private const int SectorSize = 512;
    private const int ChunkSize = 8 << 20; // 8 MiB, multiple of sector size

    private readonly RawDiskReader _reader;
    private readonly IByteSource _source;
    /// <summary>Optional: returns true when the cluster at this byte offset is allocated to live data (skip it).</summary>
    public Func<long, bool>? IsOffsetAllocated { get; init; }
    /// <summary>Carve plain text runs (off by default: noisy, low confidence).</summary>
    public bool CarveText { get; init; }

    public DeepScanner(RawDiskReader reader)
    {
        _reader = reader;
        _source = new RawDiskByteSource(reader);
    }

    public async Task<List<RecoverableFile>> ScanAsync(
        IProgress<ScanProgress>? progress, PauseToken? pause, CancellationToken ct)
        => await Task.Run(() => Scan(progress, pause, ct), ct).ConfigureAwait(false);

    private List<RecoverableFile> Scan(IProgress<ScanProgress>? progress, PauseToken? pause, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<RecoverableFile>();
        long volumeLength = _reader.Length;
        var chunk = new byte[ChunkSize];
        long pos = 0;
        long lastReport = 0;
        long id = 1;

        Log.Info("DeepScanner", $"Deep scan started: {volumeLength / (1 << 30)} GiB volume");

        while (pos < volumeLength)
        {
            ct.ThrowIfCancellationRequested();
            pause?.WaitIfPaused(ct);

            int toRead = (int)Math.Min(ChunkSize, volumeLength - pos);
            int got;
            try
            {
                got = _reader.ReadAt(pos, chunk, 0, toRead);
            }
            catch (IOException ex)
            {
                // Unreadable region (bad sectors): skip one chunk, keep scanning.
                Log.Warn("DeepScanner", $"Read error at {pos}: {ex.Message}; skipping chunk");
                pos += ChunkSize;
                continue;
            }
            if (got < SectorSize) break;

            long chunkEnd = pos + got;
            for (int off = 0; off + SectorSize <= got; off += SectorSize)
            {
                long abs = pos + off;
                if (IsOffsetAllocated != null && IsOffsetAllocated(abs)) continue;

                var found = TryCarveAt(chunk.AsSpan(off, SectorSize), abs, ref id);
                if (found != null)
                {
                    results.Add(found);
                    // Skip past the carved file to avoid re-detecting embedded content.
                    long fileEnd = abs + found.Size;
                    if (fileEnd >= chunkEnd)
                    {
                        pos = (fileEnd + SectorSize - 1) / SectorSize * SectorSize;
                        goto continueOuter;
                    }
                    off = (int)((fileEnd - pos + SectorSize - 1) / SectorSize * SectorSize) - SectorSize;
                }
            }
            pos = chunkEnd;
            continueOuter:

            if (pos - lastReport >= (64 << 20) || pos >= volumeLength)
            {
                lastReport = pos;
                progress?.Report(new ScanProgress
                {
                    Phase = "Deep scan (signature carving)",
                    BytesProcessed = Math.Min(pos, volumeLength),
                    BytesTotal = volumeLength,
                    FilesFound = results.Count,
                    Elapsed = started.Elapsed
                });
            }
        }

        Log.Info("DeepScanner", $"Deep scan finished: {results.Count} files in {started.Elapsed.TotalMinutes:F1} min");
        return results;
    }

    private RecoverableFile? TryCarveAt(ReadOnlySpan<byte> sector, long absOffset, ref long id)
    {
        foreach (var spec in FormatSpecs.All)
        {
            bool headerMatch = false;
            if (spec.Name == "MP4/MOV")
            {
                headerMatch = FormatSpecs.LooksLikeMp4(sector);
            }
            else
            {
                foreach (var h in spec.Headers)
                {
                    if (sector.Length >= h.Length && sector[..h.Length].SequenceEqual(h)) { headerMatch = true; break; }
                }
            }
            // Bare MP3 frames (no ID3 tag) are matched by sync bits.
            if (!headerMatch && spec.Name == "MP3" && sector.Length >= 4 && FormatSpecs.Mp3FrameLength(sector) > 0)
                headerMatch = true;
            if (!headerMatch) continue;

            CarveMeasure? measure;
            try { measure = spec.Measure(_source, absOffset); }
            catch (Exception ex)
            {
                Log.Debug("DeepScanner", $"Measure {spec.Name} at {absOffset} failed: {ex.Message}");
                continue;
            }
            if (measure == null || measure.Length <= 0 || measure.Length > spec.MaxLength) continue;

            var file = new RecoverableFile
            {
                Id = id++,
                FileName = $"carved_{absOffset / SectorSize:D10}.{measure.Extension}",
                OriginalPath = "",
                Size = measure.Length,
                Source = FileSource.Carved,
                OverwrittenFraction = 0, // carved data was just read and parsed from disk
            };
            file.Category = RecoverableFile.CategoryFromExtension(measure.Extension);
            file.Extents.Add(new FileExtent(absOffset, measure.Length));
            file.HealthNotes.Add(measure.StructureValid
                ? $"{spec.Name}: structure validated during carving"
                : $"{spec.Name}: header found but structure incomplete");
            file.HealthNotes.AddRange(measure.Notes);
            if (!measure.StructureValid) file.HealthNotes.Add("Carved without full structural validation");
            file.HealthNotes.Add("Carved file: original name, path and timestamps are not recoverable from raw data");
            return file;
        }
        return null;
    }
}
