using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.IO;
using System.Threading;
using System;

namespace Tanks.Net;

public struct Stdout : ILog
{
    private struct LoggerState
    {
        public LoggerState(LogLevel minLevel, MpmcRing<LogMessage> ring, StreamWriter writer, char[][] prefix)
        {
            MinLevel = minLevel;
            Ring = ring;
            Writer = writer;
            Prefix = prefix;
        }
        public readonly LogLevel MinLevel;
        public readonly MpmcRing<LogMessage> Ring;
        public readonly StreamWriter Writer; // consumer thread only
        public readonly char[][] Prefix;
    }

    // Box it for reuse in boxed contexts
    // If callers want to avoid boxing they can do
    //     class Example<TLogger> where TLogger : struct, ILog { }
    //     var example = new Example<StandardOutputLogger>();
    public static readonly ILog Instance = new Stdout();

    private static LoggerState s_state;
    private static Thread s_consumer = null!;
    private static int s_stopping;
    private static long s_dropped;

    static Stdout()
    {
        char[] TracePrefix = $"{AnsiColors.Gray}[trce]{AnsiColors.Reset} ".ToCharArray();
        char[] DebugPrefix = $"{AnsiColors.Gray}[dbug]{AnsiColors.Reset} ".ToCharArray();
        char[] InformationPrefix = $"{AnsiColors.LtGreen}[info]{AnsiColors.Reset} ".ToCharArray();
        char[] WarningPrefix = $"{AnsiColors.LtYellow}[warn]{AnsiColors.Reset} ".ToCharArray();
        char[] ErrorPrefix = $"{AnsiColors.LtRed}[fail]{AnsiColors.Reset} ".ToCharArray();
        var prefix = new char[(int)LogLevel.None + 1][];
        prefix[(int)LogLevel.Trace] = TracePrefix;
        prefix[(int)LogLevel.Debug] = DebugPrefix;
        prefix[(int)LogLevel.Information] = InformationPrefix;
        prefix[(int)LogLevel.Warning] = WarningPrefix;
        prefix[(int)LogLevel.Error] = ErrorPrefix;
        prefix[(int)LogLevel.None] = new char[0];

        LogLevel minLevel =
#if DEBUG
#if TRACE
            LogLevel.Trace;
#else
            LogLevel.Debug;
#endif
#else
            LogLevel.Information;
#endif
        if (Environment.GetEnvironmentVariable("CONSOLELOGGER_MINLEVEL") is string value &&
            Enum.TryParse(typeof(LogLevel), value, out object? enumValue) &&
            enumValue is LogLevel level)
        {
            minLevel = level;
        }

        var ring = new MpmcRing<LogMessage>(4096);
        var writer = new StreamWriter(
            Console.OpenStandardOutput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false));

        s_state = new LoggerState(minLevel, ring, writer, prefix);

        s_consumer = new Thread(ConsumerLoop)
        {
            IsBackground = true,
            Name = "stdout-logger",
        };
        s_consumer.Start();

        AppDomain.CurrentDomain.ProcessExit += static (s, e) =>
        {
            ref var ctx = ref s_state;
            try
            {
                Volatile.Write(ref s_stopping, 1);
                bool joined = s_consumer.Join(TimeSpan.FromSeconds(2));

                if (joined)
                {
                    while (ctx.Ring.TryDequeue(out var msg))
                        Write(ref ctx, ref msg);
                    long dropped = Interlocked.Read(ref s_dropped);
                    if (dropped > 0)
                        ctx.Writer.WriteLine($"[logger] dropped {dropped} message(s) under load");
                    ctx.Writer.Flush();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error cleaning up in stdout logger: {ex}");
            }
        };
    }

    public Stdout()
    {
    }

    public LogLevel MinLevel => s_state.MinLevel;

    public bool IsEnabled(LogLevel level)
    {
        return level >= s_state.MinLevel && level < LogLevel.None;
    }

    private static void ConsumerLoop()
    {
        ref var ctx = ref s_state;
        var sw = new SpinWait();
        LogMessage msg;

        while (Volatile.Read(ref s_stopping) == 0)
        {
            try
            {
                if (ctx.Ring.TryDequeue(out msg))
                {
                    do
                    {
                        Write(ref ctx, ref msg);
                    }
                    while (ctx.Ring.TryDequeue(out msg));

                    ctx.Writer.Flush();
                    sw.Reset();
                }
                else
                {
                    sw.SpinOnce();
                }
            }
            catch
            {
                sw.SpinOnce();
            }
        }

        try
        {
            while (ctx.Ring.TryDequeue(out msg))
            {
                Write(ref ctx, ref msg);
            }
            ctx.Writer.Flush();
        }
        catch
        {
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Trace(string? message) => LogCore(LogLevel.Trace, message);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Debug(string? message) => LogCore(LogLevel.Debug, message);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Info(string? message) => LogCore(LogLevel.Information, message);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Warn(string? message) => LogCore(LogLevel.Warning, message);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Error(string? message) => LogCore(LogLevel.Error, message);

    private static void LogCore(LogLevel level, string? message)
    {
        ref var ctx = ref s_state;

        if (level < ctx.MinLevel || level >= LogLevel.None) return; // not enabled
        if (string.IsNullOrEmpty(message)) return;

        var msg = new LogMessage(level, message);
        if (!ctx.Ring.TryEnqueue(msg))
        {
            Interlocked.Increment(ref s_dropped);
        }
    }

    private static void LogCore(ref LogMessage message)
    {
        ref var ctx = ref s_state;

        if (message.Level < ctx.MinLevel || message.Level >= LogLevel.None)
        {
            message.Return();
            return; // not enabled
        }

        if (ctx.Ring.TryEnqueue(message))
        {
            // ownership transferred to the ring
            message.State = null;
            message.Length = 0;
        }
        else
        {
            Interlocked.Increment(ref s_dropped);
            message.Return();
        }
    }

    private static void Write(ref LoggerState ctx, ref LogMessage message)
    {
        System.Diagnostics.Debug.Assert(message.Level >= LogLevel.Trace && message.Level < LogLevel.None);

        if (message.State is string[] strings)
        {
            System.Diagnostics.Debug.Assert(strings.Length >= message.Length);
            int n = message.Length - 1;
            if (n >= 0)
            {
                ctx.Writer.Write(ctx.Prefix[(int)message.Level]);
                for (int i = 0; i < n; i++)
                    ctx.Writer.Write(strings[i]);
                ctx.Writer.WriteLine(strings[n]);
                Array.Clear(strings, 0, n + 1); // let GC reclaim the strings
            }
            ArrayPool<string>.Shared.Return(strings);
            message.State = null;
            message.Length = 0;
        }
        else if (message.State is string value)
        {
            ctx.Writer.Write(ctx.Prefix[(int)message.Level]);
            ctx.Writer.WriteLine(value);
            message.State = null;
            message.Length = 0;
        }
        else if (message.State is char[] chars)
        {
            System.Diagnostics.Debug.Assert(chars.Length >= message.Length);
            if (message.Length > 0)
            {
                ctx.Writer.Write(ctx.Prefix[(int)message.Level]);
                ctx.Writer.WriteLine(chars, 0, message.Length);
            }
            ArrayPool<char>.Shared.Return(chars);
            message.State = null;
            message.Length = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Log(ref LogMessage message)
    {
        LogCore(ref message);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LogTrace(string? message)
    {
        LogCore(LogLevel.Trace, message);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LogDebug(string? message)
    {
        LogCore(LogLevel.Debug, message);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LogInformation(string? message)
    {
        LogCore(LogLevel.Information, message);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LogWarning(string? message)
    {
        LogCore(LogLevel.Warning, message);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LogError(string? message)
    {
        LogCore(LogLevel.Error, message);
    }
}
