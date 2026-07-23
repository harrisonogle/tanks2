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
}
