namespace Tanks.Net;

public unsafe interface IDatagramTransport
{
    // The local endpoint, if bound
    public ref readonly NetAddress LocalEndPoint { get; }

    // Bind to a local address; required before send/receive
    void Bind(in NetAddress local);

    // Send one datagram. Returns false if the OS refuses (queue full, etc.)
    // or the destination family isn't supported by the underlying socket.
    // NOTE: The <c>destination</c> is a <c>ref</c> but implementations of
    //       this method MUST NOT not modify the argument. It's just to
    //       guarantee zero-copy (avoid defensive copies from the runtime).
    // TODO: Consider <c>in NetAddress destination</c> if this goes public.
    bool TrySend(byte* datagram, int length, ref NetAddress destination);

    // Try to receive one datagram into the buffer. Returns false if nothing
    // pending (in which case `source` is left untouched). Never allocates on
    // the receive path.
    bool TryReceive(
        byte* buffer,
        int length,
        out int bytesRead,
        ref NetAddress source);

    bool PollRead(int microseconds);

    void Close();
}
