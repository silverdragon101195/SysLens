using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace SysLens.Native;

/// <summary>A disk as Windows numbers it: \\.\PhysicalDrive<see cref="Number"/>, Disk N in Task Manager.</summary>
/// <param name="Product">The product name the disk reports; a RAID volume has its own, such as "Raid 0 Volume".</param>
/// <param name="Serial">The serial number; an Intel RST volume reports its name here.</param>
/// <param name="IsRaid">Whether the disk is a volume a RAID controller makes of other drives.</param>
/// <param name="Size">In bytes; 0 when the disk does not say, such as a card reader without a card.</param>
/// <param name="Location">Where the disk sits on its controller; null for a USB disk and a disk on no controller.</param>
internal sealed record PhysicalDisk(int Number, string Product, string Serial, bool IsRaid, ulong Size, ScsiLocation? Location);

/// <summary>Where a disk or drive sits: on the controller at \\.\Scsi<see cref="Port"/>:, at a path and target.</summary>
internal readonly record struct ScsiLocation(int Port, int Path, int Target);

/// <summary>A disk's I/O counters, as Windows' partition manager keeps them for Task Manager.</summary>
/// <param name="IdleTime">Time with no I/O in flight, in 100 ns units.</param>
/// <param name="QueryTime">When the counters were read, in 100 ns units.</param>
internal readonly record struct DiskCounters(long BytesRead, long BytesWritten, long IdleTime, long QueryTime);

/// <summary>Reads the disks Windows numbers, their I/O counters and the drive letters on them. Any user may.</summary>
internal static unsafe partial class PhysicalDisks
{
    // Windows numbers disks from 0 and reuses the lowest free number, but a removed disk can leave a gap.
    private const int MaxDisks = 64;

    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const uint IoctlDiskGetDriveGeometryEx = 0x700A0;
    private const uint IoctlScsiGetAddress = 0x41018;
    private const uint IoctlDiskPerformance = 0x70020;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x560000;

    // STORAGE_PROPERTY_QUERY, all zeros for the StorageDeviceProperty standard query.
    private const int PropertyQuerySize = 12;
    private const int BusTypeUsb = 7;
    private const int BusTypeRaid = 8;
    private const int MaxExtents = 16;
    private const int DiskExtentSize = 24;
    private const uint DriveRemovable = 2;
    private const uint DriveFixed = 3;

    /// <summary>The disks present now, by number.</summary>
    public static List<PhysicalDisk> Enumerate()
    {
        var disks = new List<PhysicalDisk>();
        Span<byte> descriptor = stackalloc byte[1024];
        Span<byte> geometry = stackalloc byte[256];
        for (var number = 0; number < MaxDisks; number++)
        {
            using var disk = DeviceIo.Open(DrivePath(number));
            descriptor.Clear();
            if (disk.IsInvalid || !DeviceIo.Control(disk, IoctlStorageQueryProperty, descriptor, PropertyQuerySize))
                continue;

            // STORAGE_DEVICE_DESCRIPTOR: offsets of the product and serial strings at 16 and 24, the bus type at 28.
            var busType = BinaryPrimitives.ReadInt32LittleEndian(descriptor[28..]);

            // DISK_GEOMETRY_EX: the 24-byte DISK_GEOMETRY, then the size in bytes.
            var size = DeviceIo.Control(disk, IoctlDiskGetDriveGeometryEx, geometry)
                ? BinaryPrimitives.ReadUInt64LittleEndian(geometry[24..])
                : 0;
            // The USB mass storage driver answers port 0 whatever port numbers the controllers have.
            var location = busType != BusTypeUsb && DeviceIo.Query(disk, IoctlScsiGetAddress, out ScsiAddress address)
                ? new ScsiLocation(address.PortNumber, address.PathId, address.TargetId)
                : (ScsiLocation?)null;

            disks.Add(new PhysicalDisk(number, Text(descriptor, 16), Text(descriptor, 24), busType == BusTypeRaid, size, location));
        }
        return disks;
    }

    /// <summary>The disk's counters, or null when it keeps none, such as a card reader without a card.</summary>
    public static DiskCounters? ReadCounters(int number)
    {
        using var disk = DeviceIo.Open(DrivePath(number));
        return !disk.IsInvalid && DeviceIo.Query(disk, IoctlDiskPerformance, out DiskPerformance counters)
            ? new DiskCounters(counters.BytesRead, counters.BytesWritten, counters.IdleTime, counters.QueryTime)
            : null;
    }

    /// <summary>
    /// The drive letters on each disk, such as "C: D:", by disk number. A volume that spans disks is on each of them.
    /// </summary>
    public static Dictionary<int, string> DriveLetters()
    {
        var letters = new Dictionary<int, SortedSet<char>>();
        Span<byte> extents = stackalloc byte[8 + MaxExtents * DiskExtentSize];
        var drives = GetLogicalDrives();
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            // Network and optical drives sit on no disk.
            if ((drives & (1u << (letter - 'A'))) == 0 || GetDriveTypeW($@"{letter}:\") is not (DriveFixed or DriveRemovable))
                continue;

            using var volume = DeviceIo.Open($@"\\.\{letter}:");
            if (volume.IsInvalid || !DeviceIo.Control(volume, IoctlVolumeGetVolumeDiskExtents, extents))
                continue;

            // VOLUME_DISK_EXTENTS: the count, then from offset 8 a DISK_EXTENT per extent, led by its disk number.
            var count = Math.Min(BinaryPrimitives.ReadInt32LittleEndian(extents), MaxExtents);
            for (var i = 0; i < count; i++)
            {
                // A RAM drive reports a disk that does not exist.
                var number = BinaryPrimitives.ReadInt32LittleEndian(extents[(8 + i * DiskExtentSize)..]);
                if (number is < 0 or >= MaxDisks)
                    continue;
                if (!letters.TryGetValue(number, out var onDisk))
                    letters[number] = onDisk = [];
                onDisk.Add(letter);
            }
        }
        return letters.ToDictionary(e => e.Key, e => string.Join(" ", e.Value.Select(l => $"{l}:")));
    }

    private static string DrivePath(int number) => $@"\\.\PhysicalDrive{number}";

    /// <summary>The NUL-terminated ASCII string the descriptor field at <paramref name="field"/> points to.</summary>
    private static string Text(ReadOnlySpan<byte> descriptor, int field)
    {
        var offset = BinaryPrimitives.ReadInt32LittleEndian(descriptor[field..]);
        if (offset <= 0 || offset >= descriptor.Length)
            return "";
        var text = descriptor[offset..];
        var end = text.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? text : text[..end]).Trim();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScsiAddress
    {
        public uint Length;
        public byte PortNumber;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DiskPerformance
    {
        public long BytesRead;
        public long BytesWritten;
        public long ReadTime;
        public long WriteTime;
        public long IdleTime;
        public uint ReadCount;
        public uint WriteCount;
        public uint QueueDepth;
        public uint SplitCount;
        public long QueryTime;
        public uint StorageDeviceNumber;
        public fixed char StorageManagerName[8];
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetLogicalDrives();

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveTypeW(string root);
}
