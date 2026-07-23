using System.Buffers;
using System.Runtime.CompilerServices;
using System;

namespace Tanks;

public struct LogMessage
{
    public LogMessage(LogLevel level, ReadOnlySpan<char> message)
    {
        Level = level;
        Length = message.Length;
        State = ArrayPool<char>.Shared.Rent(Length);
        message.CopyTo((char[])State!);
    }

    public LogMessage(LogLevel level, string? message)
    {
        Level = level;
        Length = 1;
        State = message;
    }

    public LogLevel Level;
    public int Length;
    public object? State;

    public void Return()
    {
        if (State is char[] chars)
        {
            ArrayPool<char>.Shared.Return(chars);
            State = null;
            Length = 0;
        }
        else if (State is string)
        {
            State = null;
            Length = 0;
        }
        else if (State is string[] strings)
        {
            Array.Clear(strings, 0, Length); // let GC reclaim the strings
            ArrayPool<string>.Shared.Return(strings);
            State = null;
            Length = 0;
        }
    }
}

// Logger abstraction so Unity dependency does not leak into components.
// Keeps the codebase unit-testable without Unity or NuGetForUnity (for ILogger).
//
// Interpolated call sites bind to the handler overloads (C# 10+), which skip
// all interpolation work when the level is disabled. Implementations only
// need IsEnabled + the five string methods; the handler overloads have
// default implementations that forward.
public interface ILog
{
    public bool IsEnabled(LogLevel level);

    public void Log(ref LogMessage message); // implementation must clear it to default upon ownership transfer (splice)

    public void LogTrace(string? message);
    public void LogDebug(string? message);
    public void LogInformation(string? message);
    public void LogWarning(string? message);
    public void LogError(string? message);

    public void LogTrace([InterpolatedStringHandlerArgument("")] ref TraceLogHandler message)
    {
        if (message.Enabled) Log(ref message.Builder.Value);
    }

    public void LogDebug([InterpolatedStringHandlerArgument("")] ref DebugLogHandler message)
    {
        if (message.Enabled) Log(ref message.Builder.Value);
    }

    public void LogInformation([InterpolatedStringHandlerArgument("")] ref InformationLogHandler message)
    {
        if (message.Enabled) Log(ref message.Builder.Value);
    }

    public void LogWarning([InterpolatedStringHandlerArgument("")] ref WarningLogHandler message)
    {
        if (message.Enabled) Log(ref message.Builder.Value);
    }

    public void LogError([InterpolatedStringHandlerArgument("")] ref ErrorLogHandler message)
    {
        if (message.Enabled) Log(ref message.Builder.Value);
    }
}

/*
// NOTE: Unity also pipes stdout/stderr to specific file(s), so that works without actually wrapping Unity types
public sealed class UnityLog : ILog
{
    // ...
}
*/
