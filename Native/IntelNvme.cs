using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SysLens.Native;

/// <summary>An NVMe drive Intel RST reaches but Windows does not list, such as a drive of an NVMe RAID volume.</summary>
/// <param name="Serial">The serial number the drive reports.</param>
/// <param name="NamespaceIds">
/// The NGUID and EUI-64 of its first namespace, in the form Windows and Intel RST give for an NVMe drive's serial
/// number, such as "0025_3851_4140_390B.".
/// </param>
/// <param name="WarningTemperature">In °C, the composite temperature from which the drive warns; null when unset.</param>
/// <param name="CriticalTemperature">In °C, the composite temperature the drive takes as critical; null when unset.</param>
internal sealed record RstNvmeDrive(ScsiLocation Location, string Model, string Serial, IReadOnlyList<string> NamespaceIds,
    float? WarningTemperature, float? CriticalTemperature);

/// <summary>An NVMe drive's SMART / Health Information log.</summary>
/// <param name="Temperature">The composite temperature, in °C.</param>
/// <param name="Sensors">Temperature sensors 1 to 8, in °C; null for those the drive does not have.</param>
/// <param name="DataRead">In bytes.</param>
/// <param name="DataWritten">In bytes.</param>
internal sealed record NvmeHealth(float Temperature, float?[] Sensors, int AvailableSpare, int AvailableSpareThreshold,
    int PercentageUsed, double DataRead, double DataWritten, double PowerCycles, double PowerOnHours);

/// <summary>
/// Sends read-only NVMe admin commands through the NVMe pass-through of Intel RST's driver, which reaches every NVMe
/// drive behind it, the drives of an NVMe RAID volume too. Needs administrator rights.
/// </summary>
internal static class IntelNvme
{
    private const uint PassThrough = 0xF0002808;
    private const uint TimeoutSeconds = 10;

    // INTEL_NVME_PAYLOAD: version, path, target and LUN, the 64-byte NVMe command, the completion entry, the queue, the
    // sizes of the parameter and return buffers, then reserved bytes. The data follows it.
    private const int PayloadSize = 136;
    private const int DataSize = 4096;

    private const byte IdentifyOpcode = 0x06;
    private const byte GetLogPageOpcode = 0x02;
    private const uint IdentifyNamespace = 0;
    private const uint IdentifyController = 1;

    // Log page 2, read whole: 512 bytes, given as the number of dwords less one from bit 16 on.
    private const uint HealthLog = 0x02 | ((512 / 4 - 1) << 16);

    // NVMe counts data in units of a thousand 512-byte sectors and temperatures in kelvin.
    private const double DataUnit = 512_000;
    private const float Kelvin = 273.15f;

    // Intel RST gives each NVMe drive a path of its own, at target 0.
    private const int MaxPaths = 16;

    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("IntelNvm");

    /// <summary>
    /// The NVMe drives on the controller at \\.\Scsi<paramref name="port"/>:, leaving out the places in
    /// <paramref name="skip"/>.
    /// </summary>
    public static List<RstNvmeDrive> Find(int port, IReadOnlySet<ScsiLocation> skip)
    {
        var drives = new List<RstNvmeDrive>();
        using var controller = DeviceIo.OpenController(port);
        if (controller.IsInvalid)
            return drives;

        for (var path = 0; path < MaxPaths; path++)
        {
            var location = new ScsiLocation(port, path, 0);
            if (skip.Contains(location))
                continue;
            if (Send(controller, location, IdentifyOpcode, 0, IdentifyController) is not { } identify)
                continue;

            // Identify Controller: the serial number at 4, the model at 24, the warning and critical temperatures at 266
            // and 268. Identify Namespace: the NGUID at 104 and the EUI-64 at 120.
            var space = Send(controller, location, IdentifyOpcode, 1, IdentifyNamespace);
            string?[] ids = space is null ? [] : [WindowsId(space.AsSpan(104, 16)), WindowsId(space.AsSpan(120, 8))];
            drives.Add(new RstNvmeDrive(location, Ascii(identify.AsSpan(24, 40)), Ascii(identify.AsSpan(4, 20)),
                [.. ids.OfType<string>()], Celsius(identify.AsSpan(266)), Celsius(identify.AsSpan(268))));
        }
        return drives;
    }

    /// <summary>The drive's health log, or null when it does not answer.</summary>
    public static NvmeHealth? ReadHealth(SafeFileHandle controller, ScsiLocation location)
    {
        // The whole log is global: namespace FFFFFFFF.
        if (Send(controller, location, GetLogPageOpcode, uint.MaxValue, HealthLog) is not { } log)
            return null;

        // The composite temperature at 1; available spare, its threshold and percentage used at 3 to 5; data units read
        // and written, power cycles and power-on hours at 32, 48, 112 and 128, each 16 bytes of which the low 8 are
        // read; temperature sensors 1 to 8 from 200.
        var sensors = new float?[8];
        for (var i = 0; i < sensors.Length; i++)
            sensors[i] = Celsius(log.AsSpan(200 + 2 * i));
        return new NvmeHealth(Celsius(log.AsSpan(1)) ?? 0, sensors, log[3], log[4], log[5],
            BinaryPrimitives.ReadUInt64LittleEndian(log.AsSpan(32)) * DataUnit,
            BinaryPrimitives.ReadUInt64LittleEndian(log.AsSpan(48)) * DataUnit,
            BinaryPrimitives.ReadUInt64LittleEndian(log.AsSpan(112)),
            BinaryPrimitives.ReadUInt64LittleEndian(log.AsSpan(128)));
    }

    /// <summary>Sends an admin command; returns the data the drive answers with, or null when there is none.</summary>
    private static byte[]? Send(SafeFileHandle controller, ScsiLocation location, byte opcode, uint namespaceId, uint cdw10)
    {
        var buffer = new byte[DeviceIo.MiniportHeaderSize + PayloadSize + DataSize];
        var payload = buffer.AsSpan(DeviceIo.MiniportHeaderSize, PayloadSize);
        payload[0] = 1;
        payload[1] = (byte)location.Path;
        payload[2] = (byte)location.Target;
        // The command: the opcode leads CDW0 at 4, the namespace at 8, CDW10 at 44.
        payload[4] = opcode;
        BinaryPrimitives.WriteUInt32LittleEndian(payload[8..], namespaceId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[44..], cdw10);
        BinaryPrimitives.WriteInt32LittleEndian(payload[88..], DeviceIo.MiniportHeaderSize + PayloadSize);
        BinaryPrimitives.WriteInt32LittleEndian(payload[92..], DataSize);
        // The return code is not a status: the driver sets 1 when the drive answered. A command to a place without an
        // NVMe drive fails or leaves the data empty.
        if (DeviceIo.Miniport(controller, Signature, PassThrough, TimeoutSeconds, buffer) is null)
            return null;
        var data = buffer.AsSpan(DeviceIo.MiniportHeaderSize + PayloadSize);
        return data.ContainsAnyExcept((byte)0) ? data.ToArray() : null;
    }

    /// <summary>A kelvin reading as °C; null for 0, which NVMe uses for "none".</summary>
    private static float? Celsius(ReadOnlySpan<byte> kelvin) =>
        BinaryPrimitives.ReadUInt16LittleEndian(kelvin) is var k and > 0 ? k - Kelvin : null;

    /// <summary>
    /// An NGUID or EUI-64 as Windows writes it for an NVMe drive's serial number: hex in groups of four digits, joined by
    /// underscores, then a dot. Null when the drive does not set it.
    /// </summary>
    private static string? WindowsId(ReadOnlySpan<byte> id) =>
        id.ContainsAnyExcept((byte)0)
            ? string.Join('_', Convert.ToHexString(id).Chunk(4).Select(group => new string(group))) + "."
            : null;

    private static string Ascii(ReadOnlySpan<byte> text)
    {
        var end = text.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? text : text[..end]).Trim();
    }
}
