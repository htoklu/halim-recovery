using System.Buffers.Binary;

namespace HalimRecovery.Core.FileSystems.Ntfs;

/// <summary>Parsed NTFS boot sector (BPB). Layout per public NTFS documentation.</summary>
public sealed class NtfsBootSector
{
    public int BytesPerSector { get; private set; }
    public int SectorsPerCluster { get; private set; }
    public long TotalSectors { get; private set; }
    public long MftStartCluster { get; private set; }
    public long MftMirrorStartCluster { get; private set; }
    public int BytesPerMftRecord { get; private set; }
    public int BytesPerCluster => BytesPerSector * SectorsPerCluster;
    public long TotalClusters => TotalSectors / SectorsPerCluster;

    public static NtfsBootSector Parse(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512) throw new ArgumentException("Boot sector too small.");

        var bs = new NtfsBootSector
        {
            BytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector.Slice(11, 2)),
            SectorsPerCluster = DecodeSectorsPerCluster(sector[13]),
            TotalSectors = BinaryPrimitives.ReadInt64LittleEndian(sector.Slice(40, 8)),
            MftStartCluster = BinaryPrimitives.ReadInt64LittleEndian(sector.Slice(48, 8)),
            MftMirrorStartCluster = BinaryPrimitives.ReadInt64LittleEndian(sector.Slice(56, 8)),
        };

        // Clusters-per-MFT-record: positive = cluster count; negative n = record size 2^(-n) bytes.
        sbyte clustersPerRecord = unchecked((sbyte)sector[64]);
        bs.BytesPerMftRecord = clustersPerRecord > 0
            ? clustersPerRecord * bs.BytesPerCluster
            : 1 << -clustersPerRecord;

        if (bs.BytesPerSector is < 256 or > 8192 || bs.SectorsPerCluster <= 0 || bs.BytesPerMftRecord is < 256 or > 65536)
            throw new InvalidDataException("NTFS boot sector contains implausible values (corrupted filesystem?).");
        return bs;
    }

    // Values >= 0xF4 encode large clusters as 2^(256-value) sectors (e.g. 0xF1 => 32768? spec: 2^|sbyte|).
    private static int DecodeSectorsPerCluster(byte raw)
    {
        if (raw <= 0x80) return raw;
        return 1 << (256 - raw);
    }
}
