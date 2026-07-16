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
    // `payload` points past the metadata headroom; add evt.Metadata.Offset
    // for ApplicationData payloads.
    public delegate void InspectPipeEvent(ref PipeEvent evt, byte* payload);

    // Drains RX events until one of `kind` arrives (inspect runs while the
    // slot is still valid) or the timeout elapses. Returns every slot.
    public static bool WaitFor(
        in NetworkQueue rx,
        PipeEventKind kind,
        TimeSpan timeout,
        InspectPipeEvent? inspect = null)
    {
        IPipeReader reader = rx.Reader;
        var sw = Stopwatch.StartNew();
        var evt = default(PipeEvent);
        while (sw.Elapsed < timeout)
        {
            while (reader.TryDequeue(ref evt))
            {
                try
                {
                    if (evt.Metadata.Kind == kind)
                    {
                        inspect?.Invoke(ref evt, reader.Deref(ref evt));
                        return true;
                    }
                }
                finally
                {
                    reader.Return(evt.Buffer);
                }
            }
            Thread.Sleep(5);
        }
        return false;
    }
}
