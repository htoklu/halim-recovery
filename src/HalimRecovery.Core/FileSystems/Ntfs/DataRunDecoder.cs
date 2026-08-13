namespace HalimRecovery.Core.FileSystems.Ntfs;

/// <summary>One decoded NTFS data run. Lcn = -1 means a sparse run (no disk clusters).</summary>
public readonly record struct DataRun(long Lcn, long ClusterCount);

/// <summary>
/// Decodes NTFS data run lists.
/// Format: each run starts with a header byte — low nibble = size of the length field,
/// high nibble = size of the (signed, delta-encoded) LCN offset field. Header 0x00 terminates.
/// </summary>
public static class DataRunDecoder
{
    public static List<DataRun> Decode(ReadOnlySpan<byte> runData)
    {
        var runs = new List<DataRun>();
        long currentLcn = 0;
        int pos = 0;

        while (pos < runData.Length)
        {
            byte header = runData[pos++];
            if (header == 0) break;

            int lengthSize = header & 0x0F;
            int offsetSize = (header >> 4) & 0x0F;
            if (lengthSize == 0 || lengthSize > 8 || offsetSize > 8) break;             // malformed
            if (pos + lengthSize + offsetSize > runData.Length) break;                  // truncated

            long clusterCount = ReadUnsigned(runData.Slice(pos, lengthSize));
            pos += lengthSize;

            if (offsetSize == 0)
            {
                runs.Add(new DataRun(-1, clusterCount)); // sparse run
                continue;
            }

            long delta = ReadSigned(runData.Slice(pos, offsetSize));
            pos += offsetSize;
            currentLcn += delta;
            if (clusterCount <= 0 || currentLcn < 0) break;                             // malformed
            runs.Add(new DataRun(currentLcn, clusterCount));
        }
        return runs;
    }

    private static long ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (int i = bytes.Length - 1; i >= 0; i--) value = (value << 8) | bytes[i];
        return value;
    }

    private static long ReadSigned(ReadOnlySpan<byte> bytes)
    {
        long value = ReadUnsigned(bytes);
        int bits = bytes.Length * 8;
        if (bits < 64 && (value & (1L << (bits - 1))) != 0)
            value -= 1L << bits; // sign-extend
        return value;
    }
}
