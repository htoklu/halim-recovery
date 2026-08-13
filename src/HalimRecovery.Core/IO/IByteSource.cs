using HalimRecovery.Core.Models;

namespace HalimRecovery.Core.IO;

/// <summary>Random-access read abstraction shared by carving, validation and preview.</summary>
public interface IByteSource
{
    long Length { get; }
    int ReadAt(long offset, byte[] buffer, int bufferOffset, int count);
}

public sealed class ByteArraySource(byte[] data) : IByteSource
{
    public long Length => data.Length;
    public int ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        if (offset >= data.Length) return 0;
        int toCopy = (int)Math.Min(count, data.Length - offset);
        Array.Copy(data, offset, buffer, bufferOffset, toCopy);
        return toCopy;
    }
}

/// <summary>Adapts RawDiskReader to IByteSource.</summary>
public sealed class RawDiskByteSource(RawDiskReader reader) : IByteSource
{
    public long Length => reader.Length;
    public int ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
        => reader.ReadAt(offset, buffer, bufferOffset, count);
}

/// <summary>
/// Presents a RecoverableFile's content (resident data or on-disk extents) as a
/// contiguous read-only byte source, without copying whole files into memory.
/// </summary>
public sealed class ExtentByteSource : IByteSource
{
    private readonly RawDiskReader? _reader;
    private readonly byte[]? _resident;
    private readonly List<FileExtent> _extents;
    public long Length { get; }

    public ExtentByteSource(RecoverableFile file, RawDiskReader? reader)
    {
        _resident = file.ResidentData;
        _reader = reader;
        _extents = file.Extents;
        long extentTotal = _extents.Sum(e => e.Length);
        Length = _resident?.Length ?? Math.Min(file.Size > 0 ? file.Size : extentTotal, extentTotal);
    }

    public int ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        if (_resident != null)
        {
            if (offset >= _resident.Length) return 0;
            int n = (int)Math.Min(count, _resident.Length - offset);
            Array.Copy(_resident, offset, buffer, bufferOffset, n);
            return n;
        }
        if (_reader == null || offset >= Length) return 0;

        count = (int)Math.Min(count, Length - offset);
        int totalRead = 0;
        long logical = 0;
        foreach (var extent in _extents)
        {
            long extentEnd = logical + extent.Length;
            if (offset + totalRead < extentEnd && totalRead < count)
            {
                long within = offset + totalRead - logical;
                int chunk = (int)Math.Min(count - totalRead, extent.Length - within);
                int got = _reader.ReadAt(extent.Offset + within, buffer, bufferOffset + totalRead, chunk);
                totalRead += got;
                if (got < chunk) break;
            }
            logical = extentEnd;
            if (totalRead >= count) break;
        }
        return totalRead;
    }
}
