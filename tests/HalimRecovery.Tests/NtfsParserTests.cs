using System.Buffers.Binary;
using System.Text;
using HalimRecovery.Core.FileSystems;
using HalimRecovery.Core.FileSystems.Ntfs;
using HalimRecovery.Core.Models;

namespace HalimRecovery.Tests;

public class DataRunDecoderTests
{
    [Fact]
    public void SingleRun_Decoded()
    {
        // header 0x21: 1-byte length, 2-byte offset. Length=0x18 (24), LCN=0x5634.
        var runs = DataRunDecoder.Decode(new byte[] { 0x21, 0x18, 0x34, 0x56, 0x00 });
        Assert.Single(runs);
        Assert.Equal(0x5634, runs[0].Lcn);
        Assert.Equal(0x18, runs[0].ClusterCount);
    }

    [Fact]
    public void MultipleRuns_DeltaOffsetsAccumulate()
    {
        // Run1: len 8, LCN 0x100. Run2: len 4, delta +0x20 => LCN 0x120.
        var runs = DataRunDecoder.Decode(new byte[] { 0x21, 0x08, 0x00, 0x01, 0x21, 0x04, 0x20, 0x00, 0x00 });
        Assert.Equal(2, runs.Count);
        Assert.Equal(0x100, runs[0].Lcn);
        Assert.Equal(0x120, runs[1].Lcn);
    }

    [Fact]
    public void NegativeDelta_SignExtended()
    {
        // Run1: LCN 0x100. Run2: delta -0x10 (0xF0 as signed byte) => LCN 0xF0.
        var runs = DataRunDecoder.Decode(new byte[] { 0x21, 0x08, 0x00, 0x01, 0x11, 0x04, 0xF0, 0x00 });
        Assert.Equal(2, runs.Count);
        Assert.Equal(0xF0, runs[1].Lcn);
    }

    [Fact]
    public void SparseRun_MarkedWithNegativeLcn()
    {
        // header 0x01: 1-byte length, 0-byte offset => sparse.
        var runs = DataRunDecoder.Decode(new byte[] { 0x01, 0x10, 0x00 });
        Assert.Single(runs);
        Assert.Equal(-1, runs[0].Lcn);
    }

    [Fact]
    public void Garbage_DoesNotThrow()
    {
        var runs = DataRunDecoder.Decode(new byte[] { 0xFF, 0xFF, 0xFF });
        Assert.Empty(runs); // malformed input yields no runs, never throws
    }
}

public class MftRecordTests
{
    /// <summary>Builds a synthetic 1024-byte FILE record with $FILE_NAME and resident $DATA.</summary>
    private static byte[] BuildRecord(string fileName, byte[] data, bool inUse, ushort usn = 0x0042)
    {
        var buf = new byte[1024];
        var s = buf.AsSpan();
        Encoding.ASCII.GetBytes("FILE").CopyTo(buf, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(4, 2), 48);   // USA offset
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(6, 2), 3);    // USA count (usn + 2 sectors)
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(16, 2), 1);   // sequence
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(20, 2), 56);  // first attribute offset
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(22, 2), (ushort)(inUse ? 0x01 : 0x00));

        int pos = 56;

        // $FILE_NAME (0x30), resident
        var nameBytes = Encoding.Unicode.GetBytes(fileName);
        int fnValueLen = 66 + nameBytes.Length;
        int fnAttrLen = (24 + fnValueLen + 7) & ~7;
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(pos, 4), 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(pos + 4, 4), (uint)fnAttrLen);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(pos + 16, 4), (uint)fnValueLen);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(pos + 20, 2), 24);
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(pos + 24, 8), 5); // parent = root
        buf[pos + 24 + 64] = (byte)fileName.Length;
        buf[pos + 24 + 65] = 1; // Win32 namespace
        nameBytes.CopyTo(buf, pos + 24 + 66);
        pos += fnAttrLen;

        // $DATA (0x80), resident, unnamed
        int dataAttrLen = (24 + data.Length + 7) & ~7;
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(pos, 4), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(pos + 4, 4), (uint)dataAttrLen);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(pos + 16, 4), (uint)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(pos + 20, 2), 24);
        data.CopyTo(buf, pos + 24);
        pos += dataAttrLen;

        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(pos, 4), 0xFFFFFFFF); // end marker
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(24, 4), (uint)(pos + 8)); // used size

        // Apply fixup the way NTFS writes it: store originals in USA, stamp USN at sector ends.
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(48, 2), usn);
        for (int i = 1; i <= 2; i++)
        {
            int sectorEnd = i * 512;
            ushort original = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(sectorEnd - 2, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(48 + i * 2, 2), original);
            BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(sectorEnd - 2, 2), usn);
        }
        return buf;
    }

    [Fact]
    public void DeletedRecord_ParsedWithNameAndData()
    {
        var payload = Encoding.ASCII.GetBytes("hello recovered world");
        var rec = MftRecord.Parse(BuildRecord("vacation.jpg", payload, inUse: false), 100, 512);
        Assert.NotNull(rec);
        Assert.False(rec.InUse);
        Assert.Equal("vacation.jpg", rec.FileName);
        Assert.Equal(5, rec.ParentRecordNumber);
        Assert.True(rec.HasData);
        Assert.Equal(payload, rec.ResidentData);
    }

    [Fact]
    public void InUseRecord_FlagSet()
    {
        var rec = MftRecord.Parse(BuildRecord("live.txt", [1, 2, 3], inUse: true), 101, 512);
        Assert.NotNull(rec);
        Assert.True(rec.InUse);
    }

    [Fact]
    public void NonFileBuffer_ReturnsNull()
    {
        Assert.Null(MftRecord.Parse(new byte[1024], 0, 512));
    }

    [Fact]
    public void TornRecord_RejectedByFixup()
    {
        var buf = BuildRecord("torn.txt", [1], inUse: false);
        buf[510] = 0xEE; // corrupt the USN stamp of sector 1
        Assert.Null(MftRecord.Parse(buf, 0, 512));
    }
}

public class FileSystemDetectorTests
{
    private static byte[] BootSectorWithOem(string oem, string fat32Type = "", string fat16Type = "")
    {
        var b = new byte[512];
        Encoding.ASCII.GetBytes(oem.PadRight(8)).CopyTo(b, 3);
        if (fat32Type.Length > 0) Encoding.ASCII.GetBytes(fat32Type.PadRight(8)).CopyTo(b, 82);
        if (fat16Type.Length > 0) Encoding.ASCII.GetBytes(fat16Type.PadRight(8)).CopyTo(b, 54);
        return b;
    }

    [Fact]
    public void Ntfs_Detected() =>
        Assert.Equal(FileSystemKind.Ntfs, FileSystemDetector.Detect(BootSectorWithOem("NTFS")));

    [Fact]
    public void ExFat_Detected() =>
        Assert.Equal(FileSystemKind.ExFat, FileSystemDetector.Detect(BootSectorWithOem("EXFAT")));

    [Fact]
    public void Fat32_Detected() =>
        Assert.Equal(FileSystemKind.Fat32, FileSystemDetector.Detect(BootSectorWithOem("MSDOS5.0", fat32Type: "FAT32")));

    [Fact]
    public void Unknown_NotMisreported() =>
        Assert.Equal(FileSystemKind.Unknown, FileSystemDetector.Detect(new byte[512]));
}
