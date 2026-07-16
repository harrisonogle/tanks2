// Polyfill.LangVersion.cs
// Attribute polyfills for C# 10/11+ features. These only take effect when the
// Roslyn compiler is bumped past C# 9 via csc.rsp (-langversion:10 or later)
// on a per-asmdef basis. They are compile-time-only; no runtime support needed.
//
// Not usable in MonoBehaviour assemblies (Unity's script discovery breaks on
// some modern syntax there). Keep the langversion bump scoped to non-MB asmdefs.

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Marks a member as required to be initialized during object construction.
    /// C# 11 <c>required</c> keyword. Must be paired with
    /// <see cref="CompilerFeatureRequiredAttribute"/> emitted by the compiler.
    /// </summary>
    [AttributeUsage(
        AttributeTargets.Field | AttributeTargets.Property,
        AllowMultiple = false,
        Inherited = false)]
#if NOT_UNITY
internal
#else
public
#endif
    sealed class RequiredMemberAttribute : Attribute
    {
    }

    /// <summary>
    /// Indicates that the compiler feature named by <see cref="FeatureName"/>
    /// is required to consume the attributed API. C# 11 support for
    /// <c>required</c> members emits this at the type/member level.
    /// </summary>
    [AttributeUsage(
        AttributeTargets.All,
        AllowMultiple = true,
        Inherited = false)]
#if NOT_UNITY
internal
#else
public
#endif
    sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName)
        {
            FeatureName = featureName;
        }

        public string FeatureName { get; }
        public bool IsOptional { get; init; }

        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>
    /// Marks a constructor as one that initializes all <c>required</c>
    /// members, so callers don't have to. C# 11 feature.
    /// </summary>
    [AttributeUsage(
        AttributeTargets.Constructor,
        AllowMultiple = false,
        Inherited = false)]
#if NOT_UNITY
internal
#else
public
#endif
    sealed class SetsRequiredMembersAttribute : Attribute
    {
    }

    /// <summary>
    /// Used to indicate that a ref return, ref field, or ref parameter is
    /// not scoped to the enclosing method. C# 11 feature.
    /// </summary>
    [AttributeUsage(
        AttributeTargets.Method
        | AttributeTargets.Property
        | AttributeTargets.Parameter,
        AllowMultiple = false,
        Inherited = false)]
#if NOT_UNITY
internal
#else
public
#endif
    sealed class UnscopedRefAttribute : Attribute
    {
    }
}