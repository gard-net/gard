namespace Garc.Core.Protocol;

/// <summary>Tipo de frame LSP/1 (spec §2.1).</summary>
public enum FrameType : byte
{
    ControlJson = 0x01,
    DataBinary = 0x02,
    Heartbeat = 0x03,
    DataBinaryEcho = 0x04,
    Error = 0xFF,
}

public static class FrameTypeExtensions
{
    public static bool IsKnown(byte b) =>
        b is 0x01 or 0x02 or 0x03 or 0x04 or 0xFF;
}
