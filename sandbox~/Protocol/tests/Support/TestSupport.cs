using System.Diagnostics;
using Tanks;
using Tanks.Net;

namespace Tests;

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public bool IsEnabled(LogLevel level) => false;
    public void Log(ref LogMessage message) => message.Return(); // release any pooled backing
    public void LogInformation(string? message) { }
    public void LogWarning(string? message) { }
    public void LogError(string? message) { }
    public void LogDebug(string? message) { }
    public void LogTrace(string? message) { }
}

// Drops outbound datagrams at a configured rate (seeded, so runs are
// reproducible modulo thread timing). DropAll simulates a dead link.
public sealed unsafe class LossyTransport : IDatagramTransport
{
    private readonly IDatagramTransport _inner;
    private readonly Random _rng;
    private readonly double _dropRate;

    public volatile bool DropAll;
    public volatile bool DuplicateAll; // send every datagram twice (replay stress)

    public LossyTransport(IDatagramTransport inner, int seed, double dropRate)
    {
        _inner = inner;
        _rng = new Random(seed);
        _dropRate = dropRate;
    }

    public ref readonly NetAddress LocalEndPoint => ref _inner.LocalEndPoint;
    public void Bind(in NetAddress local) => _inner.Bind(local);
    public void Close() => _inner.Close();
    public bool PollRead(int microseconds) => _inner.PollRead(microseconds);

    public bool TryReceive(byte* buffer, int length, out int bytesRead, ref NetAddress source)
        => _inner.TryReceive(buffer, length, out bytesRead, ref source);

    public bool TrySend(byte* datagram, int length, ref NetAddress destination)
    {
        if (DropAll)
            return true; // swallowed; pretend the send succeeded
        if (_rng.NextDouble() < _dropRate)
            return true;
        if (DuplicateAll)
            _inner.TrySend(datagram, length, ref destination);
        return _inner.TrySend(datagram, length, ref destination);
    }
}

public static unsafe class TestHelpers
{
    // `payload` is fully resolved (headroom + metadata.Offset already applied).
    public delegate void InspectSessionEvent(ref NetworkEventMetadata metadata, byte* payload);

    // Drains RX events until one of `kind` arrives (inspect runs while the
    // slot is still valid) or the timeout elapses. Returns every slot.
    public static bool WaitFor(
        UmemPool rx,
        NetworkEventKind kind,
        TimeSpan timeout,
        InspectSessionEvent? inspect = null)
    {
        var sw = Stopwatch.StartNew();
        BufferHandle handle = default;
        while (sw.Elapsed < timeout)
        {
            while (rx.TryDequeue(ref handle))
            {
                try
                {
                    byte* slot = rx.Deref(handle);
                    var metadata = (NetworkEventMetadata*)slot;
                    if (metadata->Kind == kind)
                    {
                        inspect?.Invoke(ref *metadata, slot + NetworkConstants.NetworkEventHeadroom + metadata->Offset);
                        return true;
                    }
                }
                finally
                {
                    rx.Return(handle);
                }
            }
            Thread.Sleep(5);
        }
        return false;
    }

    public static PeerId RandomPeerId(Random rng)
    {
        byte* b = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            b[i] = (byte)rng.Next(256);
        return new PeerId(b);
    }

    // Shared session fixture pieces (RSA keygen is slow; call sparingly).
    public static SessionContext CreateSessionContext()
    {
        var crypto = TanksPeerCrypto.Instance;
        IPeerKeyPair key = crypto.GenerateKeyPair();
        NetworkHelper.GetPeerId(key.PublicKey, out PeerId pid);
        return new SessionContext(
            pid, pid, default, default,
            appProtocolId: 1, appProtocolVersion: 1,
            key, crypto);
    }
}
