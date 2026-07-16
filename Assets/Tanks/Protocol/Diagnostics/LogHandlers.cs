using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Tanks.Net;
using System;

namespace Tanks;

// Interpolated string handlers for ILog (C# 10 feature; attribute polyfills
// live in Shared/Polyfill.Csharp10.cs since netstandard2.1 lacks them).
//
// When a level is disabled, the handler's ctor reports shouldAppend=false and
// the compiler skips ALL interpolation work at the call site - no string, no
// boxing, no formatting. When enabled, the message builds in a thread-cached
// StringBuilder; the only allocation is the final string handed to the sink.
//
// Under a C# 9 compiler (Unity without a csc.rsp langversion bump) these
// overloads are never chosen and interpolated calls bind to the plain string
// overloads instead - same behavior, minus the free disabled-level exit.

public struct StringLogMessageBuilder
{
    public readonly int Capacity;
    public LogMessage Value;

    public StringLogMessageBuilder(int capacity)
    {
        ThrowHelper.ThrowIfNegativeOrZero(capacity);
        if ((capacity & (capacity - 1)) != 0)
            throw new InvalidOperationException("Capacity must be a power of 2.");
        Capacity = capacity;
        Value = default;
    }

    public void Append(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (Value.Length >= Capacity) return;
        if (Value.State is null)
        {
            Value.State = ArrayPool<string>.Shared.Rent(Capacity);
            Value.Length = 0;
        }
        ((string[])Value.State)[Value.Length] = value;
        Value.Length++;
    }

    public void AppendLiteral(string s) => Append(s);

    public void AppendFormatted(string? value) => Append(value);

    public void AppendFormatted<T>(T value) => Append(value?.ToString());

    public void AppendFormatted<T>(T value, string? format)
    {
        if (value is IFormattable f)
            Append(f.ToString(format, formatProvider: null));
        else
            Append(value?.ToString());
    }
}

[InterpolatedStringHandler]
public ref struct TraceLogHandler
{
    public StringLogMessageBuilder Builder;
    public TraceLogHandler(int literalLength, int formattedCount, ILog logger, out bool shouldAppend)
    {
        Builder = default;
        if (shouldAppend = logger.IsEnabled(LogLevel.Trace))
        {
            Builder = new StringLogMessageBuilder(1024);
            Builder.Value.Level = LogLevel.Trace;
        }
    }
    public bool Enabled => Builder.Capacity > 0;
    public void AppendLiteral(string s) => Builder.Append(s);
    public void AppendFormatted(string? value) => Builder.AppendFormatted(value);
    public void AppendFormatted<T>(T value) => Builder.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => Builder.AppendFormatted(value, format);
}

[InterpolatedStringHandler]
public ref struct DebugLogHandler
{
    public StringLogMessageBuilder Builder;
    public DebugLogHandler(int literalLength, int formattedCount, ILog logger, out bool shouldAppend)
    {
        Builder = default;
        if (shouldAppend = logger.IsEnabled(LogLevel.Debug))
        {
            Builder = new StringLogMessageBuilder(1024);
            Builder.Value.Level = LogLevel.Debug;
        }
    }
    public bool Enabled => Builder.Capacity > 0;
    public void AppendLiteral(string s) => Builder.Append(s);
    public void AppendFormatted(string? value) => Builder.AppendFormatted(value);
    public void AppendFormatted<T>(T value) => Builder.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => Builder.AppendFormatted(value, format);
}

[InterpolatedStringHandler]
public ref struct InformationLogHandler
{
    public StringLogMessageBuilder Builder;
    public InformationLogHandler(int literalLength, int formattedCount, ILog logger, out bool shouldAppend)
    {
        Builder = default;
        if (shouldAppend = logger.IsEnabled(LogLevel.Information))
        {
            Builder = new StringLogMessageBuilder(1024);
            Builder.Value.Level = LogLevel.Information;
        }
    }
    public bool Enabled => Builder.Capacity > 0;
    public void AppendLiteral(string s) => Builder.Append(s);
    public void AppendFormatted(string? value) => Builder.AppendFormatted(value);
    public void AppendFormatted<T>(T value) => Builder.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => Builder.AppendFormatted(value, format);
}

[InterpolatedStringHandler]
public ref struct WarningLogHandler
{
    public StringLogMessageBuilder Builder;
    public WarningLogHandler(int literalLength, int formattedCount, ILog logger, out bool shouldAppend)
    {
        Builder = default;
        if (shouldAppend = logger.IsEnabled(LogLevel.Warning))
        {
            Builder = new StringLogMessageBuilder(1024);
            Builder.Value.Level = LogLevel.Warning;
        }
    }
    public bool Enabled => Builder.Capacity > 0;
    public void AppendLiteral(string s) => Builder.Append(s);
    public void AppendFormatted(string? value) => Builder.AppendFormatted(value);
    public void AppendFormatted<T>(T value) => Builder.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => Builder.AppendFormatted(value, format);
}

[InterpolatedStringHandler]
public ref struct ErrorLogHandler
{
    public StringLogMessageBuilder Builder;
    public ErrorLogHandler(int literalLength, int formattedCount, ILog logger, out bool shouldAppend)
    {
        Builder = default;
        if (shouldAppend = logger.IsEnabled(LogLevel.Error))
        {
            Builder = new StringLogMessageBuilder(1024);
            Builder.Value.Level = LogLevel.Error;
        }
    }
    public bool Enabled => Builder.Capacity > 0;
    public void AppendLiteral(string s) => Builder.Append(s);
    public void AppendFormatted(string? value) => Builder.AppendFormatted(value);
    public void AppendFormatted<T>(T value) => Builder.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => Builder.AppendFormatted(value, format);
}
