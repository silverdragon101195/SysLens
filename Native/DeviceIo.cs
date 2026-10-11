using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SysLens.Native;

/// <summary>Opens devices such as \\.\PhysicalDrive0 and sends them control codes.</summary>
internal static unsafe partial class DeviceIo
{
    /// <summary>
    /// SRB_IO_CONTROL, which leads every SCSI miniport command: its own size, the signature, the timeout, the control
    /// code, the return code and the size of what follows.
    /// </summary>
    public const int MiniportHeaderSize = 28;

    private const uint IoctlScsiMiniport = 0x4D008;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    // GENERIC_READ | GENERIC_WRITE: what a miniport command needs; only an administrator gets it.
    private const uint ReadWrite = 0xC0000000;

    /// <summary>
    /// Opens a device without reading or writing it. Access 0 is enough for the control codes any user may send, and
    /// neither spins up a sleeping drive nor keeps another app from opening it.
    /// </summary>
    public static SafeFileHandle Open(string path) => CreateFileW(path, 0, FileShareReadWrite, 0, OpenExisting, 0, 0);

    /// <summary>Opens the storage controller at \\.\Scsi<paramref name="port"/>: for <see cref="Miniport"/> commands.</summary>
    public static SafeFileHandle OpenController(int port) =>
        CreateFileW($@"\\.\Scsi{port}:", ReadWrite, FileShareReadWrite, 0, OpenExisting, 0, 0);

    /// <summary>Sends a control code that takes no input and returns a <typeparamref name="T"/>.</summary>
    public static bool Query<T>(SafeFileHandle device, uint code, out T result) where T : unmanaged
    {
        T value = default;
        var done = DeviceIoControl(device, code, null, 0, &value, (uint)sizeof(T), out _, 0);
        result = value;
        return done;
    }

    /// <summary>
    /// Sends the first <paramref name="inputLength"/> bytes of <paramref name="buffer"/> and fills the buffer with what
    /// the device returns.
    /// </summary>
    public static bool Control(SafeFileHandle device, uint code, Span<byte> buffer, int inputLength = 0)
    {
        fixed (byte* data = buffer)
        {
            return DeviceIoControl(device, code, inputLength > 0 ? data : null, (uint)inputLength, data, (uint)buffer.Length,
                out _, 0);
        }
    }

    /// <summary>
    /// Sends a command to the driver of a controller from <see cref="OpenController"/>. <paramref name="buffer"/> holds
    /// the header, written here, then the command's data, which the driver overwrites with its answer. Returns the
    /// driver's return code, or null when the command did not reach it.
    /// </summary>
    public static uint? Miniport(SafeFileHandle controller, ReadOnlySpan<byte> signature, uint code, uint timeoutSeconds,
        byte[] buffer)
    {
        var header = buffer.AsSpan(0, MiniportHeaderSize);
        header.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(header, MiniportHeaderSize);
        signature.CopyTo(header[4..12]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], timeoutSeconds);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], code);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], buffer.Length - MiniportHeaderSize);
        return Control(controller, IoctlScsiMiniport, buffer, buffer.Length)
            ? BinaryPrimitives.ReadUInt32LittleEndian(header[20..])
            : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition,
        uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputSize, void* output,
        uint outputSize, out uint returned, nint overlapped);
}
