// Polyfill.cs
// Attribute polyfills that light up language/analyzer features in Unity's
// C# 9 compiler on the netstandard2.1 profile. Roslyn resolves these by
// full name, so hand-defined versions work identically to the BCL ones.

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Reserved to be used by the compiler for tracking metadata.
    /// This class should not be used by developers in source code.
    /// Required for <c>init</c>-only setters and records with init properties.
    /// </summary>
#if NOT_UNITY
internal
#else
public
#endif
    static class IsExternalInit
    {
    }

    /// <summary>
    /// Used to indicate to the compiler that a method should be called
    /// in its containing module's initializer.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
#if NOT_UNITY
internal
#else
public
#endif
    sealed class ModuleInitializerAttribute : Attribute
    {
    }

    /// <summary>
    /// Specifies that the JIT should skip zero-initialization of locals
    /// within the attributed method (or all methods in the attributed type/module).
    /// </summary>
    [AttributeUsage(
        AttributeTargets.Module
        | AttributeTargets.Class
        | AttributeTargets.Struct
        | AttributeTargets.Interface
        | AttributeTargets.Constructor
        | AttributeTargets.Method
        | AttributeTargets.Property
        | AttributeTargets.Event,
        Inherited = false)]
#if NOT_UNITY
internal
#else
public
#endif
    sealed class SkipLocalsInitAttribute : Attribute
    {
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>
    /// Specifies that the method or property will ensure that the listed
    /// field and property members have non-null values.
    /// </summary>
    [AttributeUsage(
        AttributeTargets.Method | AttributeTargets.Property,
        Inherited = false,
        AllowMultiple = true)]
#if NOT_UNITY
internal
#else
public
#endif
    sealed class MemberNotNullAttribute : Attribute
    {
        public MemberNotNullAttribute(string member)
        {
            Members = new[] { member };
        }

        public MemberNotNullAttribute(params string[] members)
        {
            Members = members;
        }

        public string[] Members { get; }
    }

    /// <summary>
    /// Specifies that the method or property will ensure that the listed
    /// field and property members have non-null values when returning with
    /// the specified <see cref="ReturnValue"/>.
    /// </summary>
    [AttributeUsage(
        AttributeTargets.Method | AttributeTargets.Property,
        Inherited = false,
        AllowMultiple = true)]
#if NOT_UNITY
internal
#else
public
#endif
    sealed class MemberNotNullWhenAttribute : Attribute
    {
        public MemberNotNullWhenAttribute(bool returnValue, string member)
        {
            ReturnValue = returnValue;
            Members = new[] { member };
        }

        public MemberNotNullWhenAttribute(bool returnValue, params string[] members)
        {
            ReturnValue = returnValue;
            Members = members;
        }

        public bool ReturnValue { get; }
        public string[] Members { get; }
    }
}