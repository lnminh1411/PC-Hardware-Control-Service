namespace PcHwCtl.Core.Devices;

/// <summary>
/// Abstraction for SMBus operations.
/// </summary>
public interface ISmbus : IDisposable
{
    /// <summary>Performs a write-direction quick command. Returns true if ACKed.</summary>
    bool Quick(byte address);

    /// <summary>Reads one byte from the specified command register. Returns -1 on failure.</summary>
    int ReadByteData(byte address, byte command);

    /// <summary>Writes one byte to the specified command register. Returns true on success.</summary>
    bool WriteByteData(byte address, byte command, byte value);

    /// <summary>Writes a 16-bit word to the specified command register. Returns true on success.</summary>
    bool WriteWordData(byte address, byte command, ushort value);

    /// <summary>Writes a block of 1..32 bytes to the specified command register. Returns true on success.</summary>
    bool WriteBlockData(byte address, byte command, ReadOnlySpan<byte> data);
}
