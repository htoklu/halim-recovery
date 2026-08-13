using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace HalimRecovery.Core.TestSupport;

/// <summary>
/// Generates structurally valid sample files for the recovery test laboratory.
/// Files carry random payloads so every generated set has unique hashes.
/// </summary>
public static class SampleFiles
{
    public static Dictionary<string, string> GenerateSet(string directory, int seed = 0)
    {
        Directory.CreateDirectory(directory);
        var rng = seed == 0 ? Random.Shared : new Random(seed);
        var manifest = new Dictionary<string, string>();

        void Emit(string name, byte[] data)
        {
            string path = Path.Combine(directory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
            manifest[name] = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        }

        byte[] RandomBytes(int n) { var b = new byte[n]; rng.NextBytes(b); return b; }

        Emit("photo_holiday.jpg", Jpeg(RandomBytes(48 * 1024)));
        Emit("screenshot.png", Png(RandomBytes(32 * 1024)));
        Emit("animation.gif", Gif(RandomBytes(16 * 1024)));
        Emit("fatura_invoice.pdf", Pdf(rng));
        Emit("report.docx", Docx("Halim Recovery test document. " + Guid.NewGuid()));
        Emit("budget.xlsx", Xlsx("Recovery budget " + Guid.NewGuid()));
        Emit("slides.pptx", Pptx("Slide text " + Guid.NewGuid()));
        Emit("backup.zip", Zip(("readme.txt", "backup " + Guid.NewGuid()), ("data.bin", Convert.ToBase64String(RandomBytes(2048)))));
        Emit("video_clip.mp4", Mp4(RandomBytes(256 * 1024)));
        Emit("song.mp3", Mp3(60));
        Emit("sound.wav", Wav(RandomBytes(100 * 1024)));
        Emit("notes.txt", Encoding.UTF8.GetBytes($"Halim Recovery test notes.\nUnique: {Guid.NewGuid()}\n" + new string('x', 5000)));
        Emit(@"subfolder\nested_doc.docx", Docx("Nested document " + Guid.NewGuid()));
        Emit(@"subfolder\nested_photo.jpg", Jpeg(RandomBytes(24 * 1024)));
        return manifest;
    }

    // --- builders: structurally valid files with arbitrary payloads ---

    public static byte[] Jpeg(byte[] payload)
    {
        var ms = new MemoryStream();
        void Seg(byte marker, byte[] data)
        {
            ms.WriteByte(0xFF); ms.WriteByte(marker);
            int len = data.Length + 2;
            ms.WriteByte((byte)(len >> 8)); ms.WriteByte((byte)len);
            ms.Write(data);
        }
        ms.WriteByte(0xFF); ms.WriteByte(0xD8);
        Seg(0xE0, "JFIF\0"u8.ToArray());
        Seg(0xDB, new byte[65]);
        Seg(0xC0, new byte[15]);
        Seg(0xC4, new byte[28]);
        Seg(0xDA, new byte[10]);
        // Entropy data: escape 0xFF bytes so the payload can't fake an EOI marker.
        foreach (byte b in payload) { ms.WriteByte(b); if (b == 0xFF) ms.WriteByte(0x00); }
        ms.WriteByte(0xFF); ms.WriteByte(0xD9);
        return ms.ToArray();
    }

    public static byte[] Png(byte[] payload)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
            ms.Write(len); ms.Write(Encoding.ASCII.GetBytes(type)); ms.Write(data); ms.Write(new byte[4]);
        }
        Chunk("IHDR", new byte[13]);
        Chunk("IDAT", payload);
        Chunk("IEND", []);
        return ms.ToArray();
    }

    public static byte[] Gif(byte[] payload)
    {
        var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        ms.Write(new byte[] { 10, 0, 10, 0, 0x00, 0, 0 });
        ms.WriteByte(0x2C);
        ms.Write(new byte[] { 0, 0, 0, 0, 10, 0, 10, 0, 0x00 });
        ms.WriteByte(2);
        for (int i = 0; i < payload.Length; i += 255)
        {
            int n = Math.Min(255, payload.Length - i);
            ms.WriteByte((byte)n);
            ms.Write(payload, i, n);
        }
        ms.WriteByte(0);
        ms.WriteByte(0x3B);
        return ms.ToArray();
    }

    public static byte[] Pdf(Random rng)
    {
        var body = $"%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                   $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
                   $"3 0 obj\n<< /Type /Page /Parent 2 0 R >>\nendobj\n" +
                   $"% padding {Convert.ToHexString(Guid.NewGuid().ToByteArray())} {new string('p', rng.Next(1000, 4000))}\n" +
                   $"xref\n0 4\ntrailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n9\n%%EOF\n";
        return Encoding.ASCII.GetBytes(body);
    }

    public static byte[] Zip(params (string Name, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                s.Write(Encoding.UTF8.GetBytes(content));
            }
        return ms.ToArray();
    }

    public static byte[] Docx(string text) => Zip(
        ("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
        ("word/document.xml", $"<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>"));

    public static byte[] Xlsx(string text) => Zip(
        ("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
        ("xl/workbook.xml", "<?xml version=\"1.0\"?><workbook/>"),
        ("xl/worksheets/sheet1.xml", "<?xml version=\"1.0\"?><worksheet/>"),
        ("xl/sharedStrings.xml", $"<?xml version=\"1.0\"?><sst><si><t>{text}</t></si></sst>"));

    public static byte[] Pptx(string text) => Zip(
        ("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
        ("ppt/presentation.xml", "<?xml version=\"1.0\"?><presentation/>"),
        ("ppt/slides/slide1.xml", $"<?xml version=\"1.0\"?><sld><t>{text}</t></sld>"));

    public static byte[] Mp4(byte[] payload)
    {
        var ms = new MemoryStream();
        void Box(string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)(data.Length + 8));
            ms.Write(len); ms.Write(Encoding.ASCII.GetBytes(type)); ms.Write(data);
        }
        Box("ftyp", "isom\0\0\0\0isomiso2"u8.ToArray());
        Box("moov", new byte[64]);
        Box("mdat", payload);
        return ms.ToArray();
    }

    public static byte[] Mp3(int frames)
    {
        var ms = new MemoryStream();
        var rng = Random.Shared;
        for (int i = 0; i < frames; i++)
        {
            ms.Write(new byte[] { 0xFF, 0xFB, 0x90, 0x00 });
            var body = new byte[417 - 4];
            rng.NextBytes(body);
            for (int j = 0; j < body.Length; j++) if (body[j] == 0xFF) body[j] = 0xFE; // no fake sync
            ms.Write(body);
        }
        return ms.ToArray();
    }

    public static byte[] Wav(byte[] payload)
    {
        var ms = new MemoryStream();
        Span<byte> len = stackalloc byte[4];
        ms.Write("RIFF"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)(4 + 24 + 8 + payload.Length));
        ms.Write(len);
        ms.Write("WAVE"u8);
        ms.Write("fmt "u8);
        BinaryPrimitives.WriteUInt32LittleEndian(len, 16); ms.Write(len);
        ms.Write(new byte[16]);
        ms.Write("data"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)payload.Length); ms.Write(len);
        ms.Write(payload);
        return ms.ToArray();
    }
}
