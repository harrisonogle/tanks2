using System.Diagnostics;

internal static class MemoryHelper
{
    // If the backing memory is a managed array, it needs to be pinned first.
    public static unsafe ref T AsRef<T>(ReadOnlySpan<byte> span) where T : unmanaged
    {
        Debug.Assert(span.Length >= sizeof(T));

        fixed (byte* ptr = span)
        {
            return ref *(T*)ptr;
        }
    }
}