using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace HalimRecovery.Tests;

/// <summary>Builds minimal but structurally correct sample files for validator tests.</summary>
public static class TestData
{
    public static byte[] MinimalJpeg()
    {
        var ms = new MemoryStream();
        void Seg(byte marker, byte[] payload)
        {
            ms.WriteByte(0xFF); ms.WriteByte(marker);
            int len = payload.Length + 2;
            ms.WriteByte((byte)(len >> 8)); ms.WriteByte((byte)len);
            ms.Write(payload);
        }
        ms.WriteByte(0xFF); ms.WriteByte(0xD8);                       // SOI
        Seg(0xE0, "JFIF\0"u8.ToArray());                              // APP0
        Seg(0xDB, new byte[65]);                                      // DQT
        Seg(0xC0, new byte[15]);                                      // SOF0
        Seg(0xC4, new byte[28]);                                      // DHT
        Seg(0xDA, new byte[10]);                                      // SOS
        ms.Write(new byte[] { 0x12, 0x34, 0xFF, 0x00, 0x56, 0x78 });  // entropy data with stuffing
        ms.WriteByte(0xFF); ms.WriteByte(0xD9);                       // EOI
        return ms.ToArray();
    }

    public static byte[] MinimalPng()
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
        Chunk("IDAT", new byte[64]);
        Chunk("IEND", []);
        return ms.ToArray();
    }

    public static byte[] MinimalGif()
    {
        var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        ms.Write(new byte[] { 10, 0, 10, 0, 0x00, 0, 0 }); // logical screen descriptor, no GCT
        ms.WriteByte(0x2C);                                 // image descriptor
        ms.Write(new byte[] { 0, 0, 0, 0, 10, 0, 10, 0, 0x00 });
        ms.WriteByte(2);                                    // LZW min code size
        ms.WriteByte(3); ms.Write(new byte[] { 1, 2, 3 });  // one sub-block
        ms.WriteByte(0);                                    // block terminator
        ms.WriteByte(0x3B);                                 // trailer
        return ms.ToArray();
    }

    public static byte[] MinimalPdf() => Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\nxref\n0 1\ntrailer\n<< >>\nstartxref\n9\n%%EOF\n");

    public static byte[] ZipWith(params (string Name, string Content)[] entries)
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

    public static byte[] MinimalMp4()
    {
        var ms = new MemoryStream();
        void Box(string type, byte[] payload)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)(payload.Length + 8));
            ms.Write(len); ms.Write(Encoding.ASCII.GetBytes(type)); ms.Write(payload);
        }
        Box("ftyp", "isom\0\0\0\0isomiso2"u8.ToArray());
        Box("moov", new byte[32]);
        Box("mdat", new byte[128]);
        return ms.ToArray();
    }

    public static byte[] MinimalMp3(int frames = 12)
    {
        // MPEG1 Layer3, 128 kbps, 44100 Hz, no padding => frame length 417 bytes.
        var ms = new MemoryStream();
        for (int i = 0; i < frames; i++)
        {
            ms.Write(new byte[] { 0xFF, 0xFB, 0x90, 0x00 });
            ms.Write(new byte[417 - 4]);
        }
        return ms.ToArray();
    }

    public static byte[] MinimalWav()
    {
        var ms = new MemoryStream();
        byte[] data = new byte[1000];
        ms.Write("RIFF"u8);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)(4 + 8 + 16 + 8 + data.Length));
        ms.Write(len);
        ms.Write("WAVE"u8);
        ms.Write("fmt "u8);
        BinaryPrimitives.WriteUInt32LittleEndian(len, 16); ms.Write(len);
        ms.Write(new byte[16]);
        ms.Write("data"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)data.Length); ms.Write(len);
        ms.Write(data);
        return ms.ToArray();
    }
}
