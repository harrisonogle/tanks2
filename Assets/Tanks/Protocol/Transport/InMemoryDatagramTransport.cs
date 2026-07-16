using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System;
using System.Threading;
using System.Net.Sockets;

namespace Tanks.Net;

public sealed class InMemoryDatagramTransport : IDatagramTransport
{
    private InMemoryDatagramTransport? _peer;
    private bool _disposed;
    private readonly ConcurrentQueue<(byte[] buffer, int length)> _inbox;
    private readonly AutoResetEvent _doorbell;
    private NetAddress _address;

    private InMemoryDatagramTransport()
    {
        _inbox = new();
        _doorbell = new(false);
    }

    public static (InMemoryDatagramTransport, InMemoryDatagramTransport) Create()
    {
        var t1 = new InMemoryDatagramTransport();
        var t2 = new InMemoryDatagramTransport();
        t1._peer = t2;
        t2._peer = t1;
        return (t1, t2);
    }

    public ref readonly NetAddress LocalEndPoint => ref _address;

    public void Bind(in NetAddress local)
    {
        _address = local;
    }

    public void Close()
    {
        if (_disposed) return;
        _disposed = true;
        _doorbell.Dispose();
        while (_inbox.TryDequeue(out var result))
        {
            ArrayPool<byte>.Shared.Return(result.buffer);
        }
    }

    public bool PollRead(int microseconds)
    {
        ThrowHelper.ThrowIfDisposed(_disposed, GetType());
        Debug.Assert(_peer is not null);
        return _inbox.Count > 0 || _doorbell.WaitOne(TimeSpan.FromSeconds(microseconds / 1_000_000.0));
    }

    public bool TryReceive(Span<byte> buffer, out int bytesRead, ref NetAddress source)
    {
        ThrowHelper.ThrowIfDisposed(_disposed, GetType());
        ThrowHelper.ThrowIfNegativeOrZero(buffer.Length);
        Debug.Assert(_peer is not null);

        if (_inbox.TryDequeue(out var item))
        {
            var src = new ReadOnlySpan<byte>(item.buffer, 0, item.length);
            bytesRead = Math.Min(src.Length, buffer.Length);
            src[..bytesRead].CopyTo(buffer);
            ArrayPool<byte>.Shared.Return(item.buffer);
            source = _peer._address;
            return true;
        }

        bytesRead = 0;
        return false;
    }

    public unsafe bool TryReceive(byte* buffer, int length, out int bytesRead, ref NetAddress source)
    {
        return TryReceive(new Span<byte>(buffer, length), out bytesRead, ref source);
    }

    public bool TrySend(ReadOnlySpan<byte> datagram, ref NetAddress destination)
    {
        ThrowHelper.ThrowIfDisposed(_disposed, GetType());
        ThrowHelper.ThrowIfGreaterThan(datagram.Length, NetworkConstants.MaxDatagramSize);
        Debug.Assert(_peer is not null);

        if (!destination.Equals(_peer._address))
        {
            return false;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(datagram.Length);
        datagram.CopyTo(buffer);
        try
        {
            _peer._inbox.Enqueue((buffer, datagram.Length));
            _peer._doorbell.Set();
        }
        catch (ObjectDisposedException) { }
        return true;
    }

    public unsafe bool TrySend(byte* datagram, int length, ref NetAddress destination)
    {
        return TrySend(new ReadOnlySpan<byte>(datagram, length), ref destination);
    }
}
