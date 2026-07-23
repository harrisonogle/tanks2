using System.Runtime.InteropServices;

namespace Tanks.Net;

// Buffer descriptor for network datagrams.
[StructLayout(LayoutKind.Explicit, Size = 8)]
public readonly struct BufferHandle
{
    internal BufferHandle(uint slotId, uint gen)
    {
        SlotId = slotId;
        Gen = gen;
    }

    [FieldOffset(0)] internal readonly uint SlotId;
    [FieldOffset(4)] internal readonly uint Gen;

    public override string ToString()
    {
        return $"{nameof(BufferHandle)}{{slot={SlotId},gen={Gen}}}";
    }
}