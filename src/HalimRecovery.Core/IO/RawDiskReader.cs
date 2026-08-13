using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HalimRecovery.Core.IO;

/// <summary>
/// Read-only, sector-aligned random access reader over a raw volume or physical disk
/// (\\.\C: or \\.\PhysicalDrive0). The source is NEVER opened for writing.
/// Reads are internally aligned to sector boundaries as required by Windows raw I/O.
/// </summary>
public sealed class RawDiskReader : IDisposable
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, out long lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove,
        out long lpNewFilePointer, uint dwMoveMethod);

    private readonly SafeFileHandle _handle;
    private readonly object _gate = new();
    public string DevicePath { get; }
    public int SectorSize { get; }
    public long Length { get; }

    public RawDiskReader(string devicePath, int sectorSize = 512)
    {
        DevicePath = devicePath;
        SectorSize = sectorSize;
        _handle = CreateFile(devicePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (_handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw err switch
            {
                5 => new UnauthorizedAccessException($"Access denied opening {devicePath}. Administrator rights are required for raw disk access."),
                2 or 3 => new FileNotFoundException($"Device {devicePath} not found. It may have been disconnected."),
                32 => new IOException($"Device {devicePath} is locked by another process."),
                _ => new IOException($"Failed to open {devicePath} (Win32 error {err}).")
            };
        }
        if (DeviceIoControl(_handle, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, out long len, 8, out _, IntPtr.Zero))
            Length = len;
        else if (GetFileSizeEx(_handle, out long fileLen))
            Length = fileLen; // regular file (disk image) instead of a device
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileSizeEx(SafeFileHandle hFile, out long lpFileSize);

    /// <summary>
    /// Reads <paramref name="count"/> bytes at absolute byte <paramref name="offset"/>.
    /// Handles sector alignment internally. Returns bytes actually read (may be short at device end).
    /// Thread-safe.
    /// </summary>
    public int ReadAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        if (count == 0) return 0;
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        long alignedStart = offset / SectorSize * SectorSize;
        int prefix = (int)(offset - alignedStart);
        long alignedEnd = (offset + count + SectorSize - 1) / SectorSize * SectorSize;
        int alignedLen = (int)(alignedEnd - alignedStart);

        IntPtr raw = Marshal.AllocHGlobal(alignedLen + SectorSize);
        try
        {
            // Align the native buffer itself to the sector size (required for some devices).
            IntPtr aligned = new((raw.ToInt64() + SectorSize - 1) / SectorSize * SectorSize);
            uint read;
            lock (_gate)
            {
                if (!SetFilePointerEx(_handle, alignedStart, out _, 0))
                    throw new IOException($"Seek failed at {alignedStart} on {DevicePath}.");
                if (!ReadFile(_handle, aligned, (uint)alignedLen, out read, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new IOException($"Read error at offset {alignedStart} on {DevicePath} (Win32 error {err}).");
                }
            }
            int available = Math.Max(0, (int)read - prefix);
            int toCopy = Math.Min(count, available);
            if (toCopy > 0) Marshal.Copy(aligned + prefix, buffer, bufferOffset, toCopy);
            return toCopy;
        }
        finally
        {
            Marshal.FreeHGlobal(raw);
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes or throws.</summary>
    public byte[] ReadExact(long offset, int count)
    {
        var buf = new byte[count];
        int got = ReadAt(offset, buf, 0, count);
        if (got != count)
            throw new IOException($"Short read at {offset}: wanted {count}, got {got}.");
        return buf;
    }

    public void Dispose() => _handle.Dispose();
}
