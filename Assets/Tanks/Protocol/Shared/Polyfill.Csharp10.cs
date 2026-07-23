// Polyfill.LangVersion.cs
// Attribute polyfills for C# 10 features. These only take effect when the
// Roslyn compiler is bumped past C# 9 via csc.rsp (-langversion:10 or later)
// on a per-asmdef basis. They are compile-time-only; no runtime support needed.
//
// Not usable in MonoBehaviour assemblies (Unity's script discovery breaks on
// some modern syntax there). Keep the langversion bump scoped to non-MB asmdefs.

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Indicates which parameter of a method the argument expression should
    /// be captured from at each call site. C# 10 feature.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
#if NOT_UNITY
internal
#else
public
#endif
    sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName)
        {
            ParameterName = parameterName;
        }

        public string ParameterName { get; }
    }

    /// <summary>
    /// Indicates the type is an interpolated string handler. C# 10 feature.
    /// Roslyn recognizes it by full name, so accessibility doesn't matter
    /// (same trick the BCL uses for downlevel embedded attributes).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
#if NOT_UNITY
internal
#else
public
#endif
    sealed class InterpolatedStringHandlerAttribute : Attribute
    {
    }

    /// <summary>
    /// Indicates which arguments of the containing method are passed to the
    /// interpolated string handler's constructor. The empty string refers to
    /// the method's receiver (<c>this</c>). C# 10 feature.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
#if NOT_UNITY
internal
#else
public
#endif
    sealed class InterpolatedStringHandlerArgumentAttribute : Attribute
    {
        public InterpolatedStringHandlerArgumentAttribute(string argument)
        {
            Arguments = new[] { argument };
        }

        public InterpolatedStringHandlerArgumentAttribute(params string[] arguments)
        {
            Arguments = arguments;
        }

        public string[] Arguments { get; }
    }
}