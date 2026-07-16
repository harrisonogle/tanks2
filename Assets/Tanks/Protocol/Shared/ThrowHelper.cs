// ThrowHelper.cs
// Polyfill for argument-validation throw helpers missing from netstandard2.1.
// Mirrors the BCL shapes (ArgumentNullException.ThrowIfNull, etc.) so calling
// code reads identically to modern .NET.
//
// Requires CallerArgumentExpressionAttribute (from Polyfill.LangVersion.cs)
// and langversion:10+ in csc.rsp.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

// Public everywhere (unlike the attribute polyfills below/beside it, which stay
// internal under NOT_UNITY): Discovery/Net consume this cross-assembly via
// ProjectReference in the sandbox, and ThrowHelper collides with nothing in the BCL.
public static class ThrowHelper
{
    /// <summary>Throws <see cref="ArgumentNullException"/> if <paramref name="argument"/> is null.</summary>
    public static void ThrowIfNull(
        [NotNull] object? argument,
        [CallerArgumentExpression("argument")] string? paramName = null)
    {
        if (argument is null)
            throw new ArgumentNullException(paramName);
    }

    /// <summary>Throws <see cref="ArgumentNullException"/> if <paramref name="argument"/> is null, or <see cref="ArgumentException"/> if empty.</summary>
    public static void ThrowIfNullOrEmpty(
        [NotNull] string? argument,
        [CallerArgumentExpression("argument")] string? paramName = null)
    {
        if (argument is null)
            throw new ArgumentNullException(paramName);
        if (argument.Length == 0)
            throw new ArgumentException("Value cannot be empty.", paramName);
    }

    /// <summary>Throws <see cref="ArgumentException"/> if <paramref name="argument"/> is null, empty, or whitespace.</summary>
    public static void ThrowIfNullOrWhiteSpace(
        [NotNull] string? argument,
        [CallerArgumentExpression("argument")] string? paramName = null)
    {
        if (argument is null)
            throw new ArgumentNullException(paramName);
        if (string.IsNullOrWhiteSpace(argument))
            throw new ArgumentException("Value cannot be empty or whitespace.", paramName);
    }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if <paramref name="value"/> is negative.</summary>
    public static void ThrowIfNegative(
        int value,
        [CallerArgumentExpression("value")] string? paramName = null)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be non-negative.");
    }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if <paramref name="value"/> is negative.</summary>
    public static void ThrowIfNegative(
        long value,
        [CallerArgumentExpression("value")] string? paramName = null)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be non-negative.");
    }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if <paramref name="value"/> is negative or zero.</summary>
    public static void ThrowIfNegativeOrZero(
        int value,
        [CallerArgumentExpression("value")] string? paramName = null)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be positive.");
    }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if <paramref name="value"/> is negative or zero.</summary>
    public static void ThrowIfNegativeOrZero(
        long value,
        [CallerArgumentExpression("value")] string? paramName = null)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be positive.");
    }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if <paramref name="value"/> is less than <paramref name="other"/>.</summary>
    public static void ThrowIfLessThan<T>(
        T value,
        T other,
        [CallerArgumentExpression("value")] string? paramName = null)
        where T : IComparable<T>
    {
        if (value.CompareTo(other) < 0)
            throw new ArgumentOutOfRangeException(paramName, value, $"Value must be >= {other}.");
    }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if <paramref name="value"/> is greater than <paramref name="other"/>.</summary>
    public static void ThrowIfGreaterThan<T>(
        T value,
        T other,
        [CallerArgumentExpression("value")] string? paramName = null)
        where T : IComparable<T>
    {
        if (value.CompareTo(other) > 0)
            throw new ArgumentOutOfRangeException(paramName, value, $"Value must be <= {other}.");
    }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if <paramref name="value"/> is greater than or equal to <paramref name="other"/>.</summary>
    public static void ThrowIfGreaterThanOrEqual<T>(
        T value,
        T other,
        [CallerArgumentExpression("value")] string? paramName = null)
        where T : IComparable<T>
    {
        if (value.CompareTo(other) >= 0)
            throw new ArgumentOutOfRangeException(paramName, value, $"Value must be <= {other}.");
    }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if <paramref name="value"/> equals <paramref name="other"/>.</summary>
    public static void ThrowIfEqual<T>(
        T value,
        T other,
        [CallerArgumentExpression("value")] string? paramName = null)
        where T : IEquatable<T>
    {
        if (value.Equals(other))
            throw new ArgumentOutOfRangeException(paramName, value, $"Value must not equal {other}.");
    }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if <paramref name="value"/> does not equal <paramref name="other"/>.</summary>
    public static void ThrowIfNotEqual<T>(
        T value,
        T other,
        [CallerArgumentExpression("value")] string? paramName = null)
        where T : IEquatable<T>
    {
        if (!value.Equals(other))
            throw new ArgumentOutOfRangeException(paramName, value, $"Value must equal {other}.");
    }

    /// <summary>Throws <see cref="ObjectDisposedException"/> if <paramref name="disposed"/> is true.</summary>
    public static void ThrowIfDisposed(bool disposed, object instance)
    {
        if (disposed)
            throw new ObjectDisposedException(instance?.GetType().FullName);
    }

    /// <summary>Throws <see cref="ObjectDisposedException"/> if <paramref name="disposed"/> is true.</summary>
    public static void ThrowIfDisposed(bool disposed, Type type)
    {
        if (disposed)
            throw new ObjectDisposedException(type?.FullName);
    }

    /// <summary>Throws <see cref="ArgumentException"/> if <paramref name="buffer"/> is shorter than <paramref name="minimumLength"/>.</summary>
    public static void ThrowIfShorterThan<T>(
        ReadOnlySpan<T> buffer,
        int minimumLength,
        [CallerArgumentExpression("buffer")] string? paramName = null)
    {
        if (buffer.Length < minimumLength)
            throw new ArgumentException($"Span must be at least {minimumLength} bytes (was {buffer.Length}).", paramName);
    }

    /// <summary>Throws <see cref="ArgumentException"/> if <paramref name="buffer"/> is shorter than <paramref name="minimumLength"/>.</summary>
    public static void ThrowIfShorterThan<T>(
        Span<T> buffer,
        int minimumLength,
        [CallerArgumentExpression("buffer")] string? paramName = null)
    {
        if (buffer.Length < minimumLength)
            throw new ArgumentException($"Span must be at least {minimumLength} bytes (was {buffer.Length}).", paramName);
    }
}