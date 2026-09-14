#if !NET7_0_OR_GREATER
namespace System.Diagnostics.CodeAnalysis;

// Compiler-recognized polyfill so netstandard2.1 builds can return refs into a struct's
// own fields (the matrix row indexer). net7.0+ supplies the real attribute.
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
internal sealed class UnscopedRefAttribute : Attribute
{
}
#endif
