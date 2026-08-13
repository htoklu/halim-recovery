using System.Buffers.Binary;
using System.Text;

namespace HalimRecovery.Core.FileSystems.Ntfs;

/// <summary>
/// A parsed NTFS MFT FILE record: header flags, best filename, timestamps,
/// unnamed $DATA stream (resident bytes or data runs).
/// </summary>
public sealed class MftRecord
{
    public const uint AttrStandardInformation = 0x10;
    public const uint AttrFileName = 0x30;
    public const uint AttrData = 0x80;

    public long RecordNumber { get; private set; }
    public ushort SequenceNumber { get; private set; }
    public bool IsValid { get; private set; }
    public bool InUse { get; private set; }
    public bool IsDirectory { get; private set; }
    /// <summary>Non-zero when this is an extension record belonging to a base record.</summary>
    public long BaseRecordNumber { get; private set; }

    public string FileName { get; private set; } = "";
    /// <summary>MFT record number of the parent directory (from $FILE_NAME).</summary>
    public long ParentRecordNumber { get; private set; } = -1;
    public DateTime? CreatedUtc { get; private set; }
    public DateTime? ModifiedUtc { get; private set; }

    public bool HasData { get; private set; }
    public long DataSize { get; private set; }
    public byte[]? ResidentData { get; private set; }
    public List<DataRun> DataRuns { get; } = new();

    /// <summary>Parses one FILE record. Returns null when the buffer is not a FILE record.</summary>
    public static MftRecord? Parse(byte[] buffer, long recordNumber, int bytesPerSector)
    {
        if (buffer.Length < 42 || buffer[0] != 'F' || buffer[1] != 'I' || buffer[2] != 'L' || buffer[3] != 'E')
            return null;

        var rec = new MftRecord { RecordNumber = recordNumber };
        if (!ApplyFixup(buffer, bytesPerSector)) return null;

        var span = buffer.AsSpan();
        rec.SequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(16, 2));
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(22, 2));
        rec.InUse = (flags & 0x01) != 0;
        rec.IsDirectory = (flags & 0x02) != 0;
        rec.BaseRecordNumber = (long)(BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(32, 8)) & 0x0000FFFFFFFFFFFF);

        int attrOffset = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(20, 2));
        int usedSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(24, 4));
        if (usedSize > buffer.Length) usedSize = buffer.Length;

        byte bestNamespace = 0xFF; // prefer Win32 (1) / Win32AndDos (3) / POSIX (0) over DOS 8.3 (2)
        while (attrOffset + 16 <= usedSize)
        {
            uint attrType = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(attrOffset, 4));
            if (attrType == 0xFFFFFFFF) break;
            int attrLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(attrOffset + 4, 4));
            if (attrLen < 16 || attrOffset + attrLen > usedSize) break;

            var attr = span.Slice(attrOffset, attrLen);
            bool nonResident = attr[8] != 0;
            int nameLength = attr[9];

            switch (attrType)
            {
                case AttrStandardInformation when !nonResident:
                {
                    var val = ResidentValue(attr);
                    if (val.Length >= 32)
                    {
                        rec.CreatedUtc = FileTimeToUtc(BinaryPrimitives.ReadInt64LittleEndian(val));
                        rec.ModifiedUtc = FileTimeToUtc(BinaryPrimitives.ReadInt64LittleEndian(val.Slice(8)));
                    }
                    break;
                }
                case AttrFileName when !nonResident:
                {
                    var val = ResidentValue(attr);
                    if (val.Length >= 66)
                    {
                        byte ns = val[65];
                        int fnLen = val[64];
                        // Rank: Win32-visible namespaces beat DOS 8.3 short names.
                        byte rank = ns switch { 1 => 0, 3 => 0, 0 => 1, 2 => 2, _ => 3 };
                        byte bestRank = bestNamespace switch { 1 => 0, 3 => 0, 0 => 1, 2 => 2, _ => 3 };
                        if (rank < bestRank || bestNamespace == 0xFF)
                        {
                            bestNamespace = ns;
                            if (66 + fnLen * 2 <= val.Length)
                                rec.FileName = Encoding.Unicode.GetString(val.Slice(66, fnLen * 2));
                            rec.ParentRecordNumber = (long)(BinaryPrimitives.ReadUInt64LittleEndian(val) & 0x0000FFFFFFFFFFFF);
                        }
                    }
                    break;
                }
                case AttrData when nameLength == 0: // unnamed stream = main file content
                {
                    rec.HasData = true;
                    if (!nonResident)
                    {
                        var val = ResidentValue(attr);
                        rec.ResidentData = val.ToArray();
                        rec.DataSize = val.Length;
                    }
                    else if (attr.Length >= 64)
                    {
                        rec.DataSize = BinaryPrimitives.ReadInt64LittleEndian(attr.Slice(48, 8)); // real size
                        int runOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr.Slice(32, 2));
                        if (runOffset > 0 && runOffset < attr.Length)
                            rec.DataRuns.AddRange(DataRunDecoder.Decode(attr.Slice(runOffset)));
                    }
                    break;
                }
            }
            attrOffset += attrLen;
        }

        rec.IsValid = true;
        return rec;
    }

    private static ReadOnlySpan<byte> ResidentValue(ReadOnlySpan<byte> attr)
    {
        if (attr.Length < 24) return default;
        int valLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(attr.Slice(16, 4));
        int valOff = BinaryPrimitives.ReadUInt16LittleEndian(attr.Slice(20, 2));
        if (valOff < 0 || valLen < 0 || valOff + valLen > attr.Length) return default;
        return attr.Slice(valOff, valLen);
    }

    /// <summary>
    /// Applies the NTFS update sequence array (fixup): the last 2 bytes of every sector
    /// are replaced on disk by the USN and must be restored from the USA before parsing.
    /// </summary>
    private static bool ApplyFixup(byte[] buffer, int bytesPerSector)
    {
        var span = buffer.AsSpan();
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4, 2));
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(6, 2));
        if (usaCount < 2 || usaOffset + usaCount * 2 > buffer.Length) return false;

        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(usaOffset, 2));
        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = i * bytesPerSector;
            if (sectorEnd > buffer.Length) break;
            ushort onDisk = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(sectorEnd - 2, 2));
            if (onDisk != usn) return false; // torn write / corrupted record
            ushort original = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(usaOffset + i * 2, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(sectorEnd - 2, 2), original);
        }
        return true;
    }

    private static DateTime? FileTimeToUtc(long fileTime)
    {
        if (fileTime <= 0) return null;
        try { return DateTime.FromFileTimeUtc(fileTime); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
