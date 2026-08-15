#if !NET5_0_OR_GREATER

// Types the C# compiler requires by name but that the older target frameworks never shipped.
// Declaring them here is the documented approach and costs nothing at runtime: the compiler only
// needs the type to exist to emit the corresponding modreq/attribute. Everything is internal, so
// two assemblies doing the same thing cannot collide.

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Marker the compiler emits for every <c>init</c> accessor - which means every positional
    /// record in this assembly. Present from .NET 5 on; absent from netstandard2.0 and
    /// .NET Framework, where records would otherwise fail to compile with CS0518.
    /// </summary>
    internal static class IsExternalInit
    {
    }
}

#endif
