using System.Buffers.Binary;
using System.Text;
using HalimRecovery.Core.IO;

namespace HalimRecovery.Core.Carving;

/// <summary>Result of measuring a candidate file at a given offset.</summary>
public sealed record CarveMeasure(long Length, bool StructureValid, string Extension, List<string> Notes);

/// <summary>A carvable file format: signature + structural measurement/validation.</summary>
public sealed class FormatSpec
{
    public required string Name { get; init; }
    public required string Extension { get; init; }
    public required byte[][] Headers { get; init; }
    public required long MaxLength { get; init; }
    /// <summary>Measures the file starting at offset; null when it is not a valid candidate.</summary>
    public required Func<IByteSource, long, CarveMeasure?> Measure { get; init; }
}

/// <summary>
/// Format specifications for carving. All parsers are defensive: bounded reads, no trust
/// in on-disk lengths. A file is only reported "structure valid" when the format's own
/// structure (header + internal layout + terminator) checks out.
/// </summary>
public static class FormatSpecs
{
    public static IReadOnlyList<FormatSpec> All { get; } = Build();

    private static List<FormatSpec> Build() =>
    [
        new() { Name = "JPEG", Extension = "jpg", MaxLength = 128 << 20,
                Headers = [[0xFF, 0xD8, 0xFF]], Measure = MeasureJpeg },
        new() { Name = "PNG", Extension = "png", MaxLength = 256 << 20,
                Headers = [[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]], Measure = MeasurePng },
        new() { Name = "GIF", Extension = "gif", MaxLength = 64 << 20,
                Headers = ["GIF87a"u8.ToArray(), "GIF89a"u8.ToArray()], Measure = MeasureGif },
        new() { Name = "PDF", Extension = "pdf", MaxLength = 256 << 20,
                Headers = ["%PDF-"u8.ToArray()], Measure = MeasurePdf },
        new() { Name = "ZIP/Office", Extension = "zip", MaxLength = 1L << 30,
                Headers = [[0x50, 0x4B, 0x03, 0x04]], Measure = MeasureZip },
        new() { Name = "MP4/MOV", Extension = "mp4", MaxLength = 8L << 30,
                Headers = [], Measure = MeasureMp4 }, // matched via ftyp check, see HeaderMatcher
        new() { Name = "MP3", Extension = "mp3", MaxLength = 512 << 20,
                Headers = ["ID3"u8.ToArray()], Measure = MeasureMp3 },
        new() { Name = "WAV", Extension = "wav", MaxLength = 4L << 30,
                Headers = ["RIFF"u8.ToArray()], Measure = MeasureWav },
    ];

    // ---------- helpers ----------

    private static byte[]? Read(IByteSource src, long offset, int count)
    {
        var buf = new byte[count];
        int got = src.ReadAt(offset, buf, 0, count);
        if (got <= 0) return null;
        if (got < count) Array.Resize(ref buf, got);
        return buf;
    }

    // ---------- JPEG ----------
    // Segment-parse until SOS, then scan entropy-coded data for the EOI marker (FFD9).
    // Length-parsed APPn segments mean embedded EXIF thumbnails are skipped correctly.
    private static CarveMeasure? MeasureJpeg(IByteSource src, long start)
    {
        long pos = start + 2;
        long max = Math.Min(src.Length, start + (128L << 20));
        bool sawSos = false;
        var notes = new List<string>();

        while (pos + 4 <= max)
        {
            var hdr = Read(src, pos, 4);
            if (hdr == null || hdr.Length < 4 || hdr[0] != 0xFF) return null;
            byte marker = hdr[1];
            if (marker == 0xFF) { pos++; continue; }            // fill byte
            if (marker == 0xD9) return new CarveMeasure(pos + 2 - start, sawSos, "jpg", notes);
            if (marker is >= 0xD0 and <= 0xD7 or 0x01) { pos += 2; continue; }

            int segLen = (hdr[2] << 8) | hdr[3];
            if (segLen < 2) return null;
            if (marker == 0xDA) // Start of Scan: raw-search for FFD9
            {
                sawSos = true;
                pos += 2 + segLen;
                var buf = new byte[1 << 20];
                while (pos < max)
                {
                    int got = src.ReadAt(pos, buf, 0, (int)Math.Min(buf.Length, max - pos));
                    if (got < 2) break;
                    for (int i = 0; i < got - 1; i++)
                    {
                        if (buf[i] != 0xFF) continue;
                        byte b2 = buf[i + 1];
                        if (b2 == 0xD9) return new CarveMeasure(pos + i + 2 - start, true, "jpg", notes);
                        // 0x00 stuffing, RSTn, or next-scan markers (progressive) are all legal mid-stream.
                    }
                    pos += got - 1; // keep 1-byte overlap for split FF D9
                }
                return null;
            }
            pos += 2 + segLen;
        }
        return null;
    }

    // ---------- PNG ----------
    private static CarveMeasure? MeasurePng(IByteSource src, long start)
    {
        long pos = start + 8;
        long max = Math.Min(src.Length, start + (256L << 20));
        bool first = true;
        var notes = new List<string>();

        while (pos + 12 <= max)
        {
            var hdr = Read(src, pos, 8);
            if (hdr == null || hdr.Length < 8) return null;
            uint len = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            string type = Encoding.ASCII.GetString(hdr, 4, 4);
            if (len > int.MaxValue || !type.All(c => char.IsAsciiLetter(c))) return null;
            if (first && (type != "IHDR" || len != 13)) return null;
            first = false;
            pos += 8 + len + 4; // data + CRC
            if (type == "IEND") return new CarveMeasure(pos - start, true, "png", notes);
        }
        return null;
    }

    // ---------- GIF ----------
    private static CarveMeasure? MeasureGif(IByteSource src, long start)
    {
        long max = Math.Min(src.Length, start + (64L << 20));
        var head = Read(src, start, 13);
        if (head == null || head.Length < 13) return null;
        long pos = start + 13;
        if ((head[10] & 0x80) != 0) pos += 3L * (1 << ((head[10] & 0x07) + 1)); // global color table

        while (pos < max)
        {
            var b = Read(src, pos, 1);
            if (b == null) return null;
            switch (b[0])
            {
                case 0x3B: return new CarveMeasure(pos + 1 - start, true, "gif", []);
                case 0x21: // extension: label + sub-blocks
                    pos += 2;
                    if (!SkipSubBlocks(src, ref pos, max)) return null;
                    break;
                case 0x2C: // image descriptor
                {
                    var desc = Read(src, pos, 10);
                    if (desc == null || desc.Length < 10) return null;
                    pos += 10;
                    if ((desc[9] & 0x80) != 0) pos += 3L * (1 << ((desc[9] & 0x07) + 1)); // local color table
                    pos += 1; // LZW minimum code size
                    if (!SkipSubBlocks(src, ref pos, max)) return null;
                    break;
                }
                default: return null;
            }
        }
        return null;

        static bool SkipSubBlocks(IByteSource src, ref long pos, long max)
        {
            while (pos < max)
            {
                var len = Read(src, pos, 1);
                if (len == null) return false;
                pos += 1 + len[0];
                if (len[0] == 0) return true;
            }
            return false;
        }
    }

    // ---------- PDF ----------
    // End = last "%%EOF" (incremental updates append new ones). Stops when no new EOF
    // appears within 8 MiB after the last one found.
    private static CarveMeasure? MeasurePdf(IByteSource src, long start)
    {
        long max = Math.Min(src.Length, start + (256L << 20));
        var eof = "%%EOF"u8.ToArray();
        var startxref = "startxref"u8.ToArray();
        long lastEof = -1;
        bool sawStartXref = false;
        var buf = new byte[1 << 20];
        long pos = start;

        while (pos < max)
        {
            if (lastEof > 0 && pos - lastEof > (8L << 20)) break;
            int got = src.ReadAt(pos, buf, 0, (int)Math.Min(buf.Length, max - pos));
            if (got < eof.Length) break;
            for (int i = 0; i <= got - 5; i++)
            {
                if (buf[i] == '%' && Matches(buf, i, eof)) lastEof = pos + i + eof.Length;
                else if (buf[i] == 's' && i <= got - 9 && Matches(buf, i, startxref)) sawStartXref = true;
            }
            pos += got - (eof.Length - 1);
        }
        if (lastEof < 0) return null;
        var notes = new List<string>();
        if (!sawStartXref) notes.Add("PDF: no startxref found (may be truncated)");
        return new CarveMeasure(lastEof - start, sawStartXref, "pdf", notes);
    }

    private static bool Matches(byte[] buf, int at, byte[] pattern)
    {
        if (at + pattern.Length > buf.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (buf[at + i] != pattern[i]) return false;
        return true;
    }

    // ---------- ZIP / DOCX / XLSX / PPTX ----------
    // Finds End Of Central Directory, verifies the central directory pointer, and
    // classifies Office documents by their internal directory names.
    private static CarveMeasure? MeasureZip(IByteSource src, long start)
    {
        long max = Math.Min(src.Length, start + (1L << 30));
        var buf = new byte[1 << 20];
        long pos = start;
        long end = -1;

        while (pos < max && end < 0)
        {
            int got = src.ReadAt(pos, buf, 0, (int)Math.Min(buf.Length, max - pos));
            if (got < 22) break;
            for (int i = 0; i <= got - 22; i++)
            {
                if (buf[i] != 0x50 || buf[i + 1] != 0x4B || buf[i + 2] != 0x05 || buf[i + 3] != 0x06) continue;
                int commentLen = buf[i + 20] | (buf[i + 21] << 8);
                long candidate = pos + i + 22 + commentLen;
                long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i + 16, 4));
                // Central directory must start inside the carved file with a PK\x01\x02 record.
                var cd = Read(src, start + cdOffset, 4);
                if (cd is [0x50, 0x4B, 0x01, 0x02] || cdOffset == 0xFFFFFFFF /* zip64 */)
                {
                    end = candidate;
                    break;
                }
            }
            pos += got - 21;
        }
        if (end < 0) return null;

        var notes = new List<string>();
        string ext = ClassifyZip(src, start, end, notes);
        return new CarveMeasure(end - start, true, ext, notes);
    }

    private static string ClassifyZip(IByteSource src, long start, long end, List<string> notes)
    {
        // Office files start with local headers naming [Content_Types].xml, word/, xl/, ppt/.
        var head = Read(src, start, (int)Math.Min(4096, end - start));
        if (head == null) return "zip";
        string text = Encoding.ASCII.GetString(head);
        bool office = text.Contains("[Content_Types].xml");
        if (text.Contains("word/")) return "docx";
        if (text.Contains("xl/")) return "xlsx";
        if (text.Contains("ppt/")) return "pptx";
        if (office)
        {
            // Directory entries may appear later; check central directory area too.
            var tail = Read(src, Math.Max(start, end - 65536), (int)Math.Min(65536, end - start));
            if (tail != null)
            {
                string t = Encoding.ASCII.GetString(tail);
                if (t.Contains("word/")) return "docx";
                if (t.Contains("xl/")) return "xlsx";
                if (t.Contains("ppt/")) return "pptx";
            }
            notes.Add("Office container detected but application type unclear");
        }
        return "zip";
    }

    // ---------- MP4 / MOV (ISO BMFF) ----------
    public static bool LooksLikeMp4(ReadOnlySpan<byte> sector)
        => sector.Length >= 12 && sector[4] == 'f' && sector[5] == 't' && sector[6] == 'y' && sector[7] == 'p';

    private static CarveMeasure? MeasureMp4(IByteSource src, long start)
    {
        long max = Math.Min(src.Length, start + (8L << 30));
        long pos = start;
        bool sawFtyp = false, sawMedia = false;
        string ext = "mp4";
        int boxCount = 0;

        while (pos + 8 <= max && boxCount < 1000)
        {
            var hdr = Read(src, pos, 16);
            if (hdr == null || hdr.Length < 8) break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            string type = Encoding.ASCII.GetString(hdr, 4, 4);
            if (!type.All(c => char.IsAsciiLetterOrDigit(c) || c == ' ')) break;

            if (size == 1 && hdr.Length >= 16)
                size = BinaryPrimitives.ReadInt64BigEndian(hdr.AsSpan(8, 8));
            else if (size == 0) break; // "rest of file" — cannot determine end
            if (size < 8 || pos + size > max) break;

            if (type == "ftyp")
            {
                sawFtyp = true;
                if (hdr.Length >= 12 && Encoding.ASCII.GetString(hdr, 8, 2) == "qt") ext = "mov";
            }
            if (type is "moov" or "mdat") sawMedia = true;
            pos += size;
            boxCount++;
        }
        if (!sawFtyp || boxCount < 2) return null;
        var notes = new List<string>();
        if (!sawMedia) notes.Add("MP4: no moov/mdat box found (likely truncated)");
        return new CarveMeasure(pos - start, sawFtyp && sawMedia, ext, notes);
    }

    // ---------- MP3 ----------
    private static readonly int[][] BitrateTable =
    {
        // MPEG1 Layer3, MPEG2/2.5 Layer3 (kbps)
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0],
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0],
    };
    private static readonly int[][] SampleRateTable =
    {
        [44100, 48000, 32000, 0], // MPEG1
        [22050, 24000, 16000, 0], // MPEG2
        [11025, 12000, 8000, 0],  // MPEG2.5
    };

    private static CarveMeasure? MeasureMp3(IByteSource src, long start)
    {
        long pos = start;
        var id3 = Read(src, pos, 10);
        if (id3 != null && id3.Length >= 10 && id3[0] == 'I' && id3[1] == 'D' && id3[2] == '3')
        {
            int size = ((id3[6] & 0x7F) << 21) | ((id3[7] & 0x7F) << 14) | ((id3[8] & 0x7F) << 7) | (id3[9] & 0x7F);
            pos += 10 + size;
        }

        long max = Math.Min(src.Length, start + (512L << 20));
        int frames = 0;
        while (pos + 4 <= max)
        {
            var h = Read(src, pos, 4);
            if (h == null || h.Length < 4) break;
            int len = Mp3FrameLength(h);
            if (len <= 0) break;
            pos += len;
            frames++;
        }
        if (frames < 3) return null; // too short to be confident it's really MP3 audio
        return new CarveMeasure(pos - start, frames >= 10, "mp3", frames < 10 ? ["MP3: very short audio stream"] : []);
    }

    public static int Mp3FrameLength(ReadOnlySpan<byte> h)
    {
        if (h[0] != 0xFF || (h[1] & 0xE0) != 0xE0) return -1;
        int versionBits = (h[1] >> 3) & 0x03;   // 3=MPEG1, 2=MPEG2, 0=MPEG2.5
        int layerBits = (h[1] >> 1) & 0x03;     // 1=Layer3
        if (versionBits == 1 || layerBits != 1) return -1; // only Layer 3
        int bitrateIdx = (h[2] >> 4) & 0x0F;
        int sampleIdx = (h[2] >> 2) & 0x03;
        int padding = (h[2] >> 1) & 0x01;
        if (bitrateIdx is 0 or 15 || sampleIdx == 3) return -1;

        bool mpeg1 = versionBits == 3;
        int bitrate = BitrateTable[mpeg1 ? 0 : 1][bitrateIdx] * 1000;
        int sampleRate = SampleRateTable[versionBits == 3 ? 0 : versionBits == 2 ? 1 : 2][sampleIdx];
        if (bitrate == 0 || sampleRate == 0) return -1;
        int factor = mpeg1 ? 144 : 72;
        return factor * bitrate / sampleRate + padding;
    }

    // ---------- WAV ----------
    private static CarveMeasure? MeasureWav(IByteSource src, long start)
    {
        var hdr = Read(src, start, 16);
        if (hdr == null || hdr.Length < 16) return null;
        if (Encoding.ASCII.GetString(hdr, 8, 4) != "WAVE") return null;
        long riffSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4, 4));
        if (riffSize < 12 || riffSize > (4L << 30)) return null;
        bool fmtOk = Encoding.ASCII.GetString(hdr, 12, 4) is "fmt " or "JUNK" or "LIST";
        return new CarveMeasure(8 + riffSize, fmtOk, "wav", fmtOk ? [] : ["WAV: unexpected first chunk"]);
    }
}
