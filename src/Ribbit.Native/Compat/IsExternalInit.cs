#if NETFRAMEWORK

// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices;

/// <summary>
/// The marker the compiler needs for `init` setters and `record` types.
///
/// .NET 8 has it; .NET Framework 4.8 does not, and the C# compiler will not
/// emit an init-only setter without it. Since this whole codebase leans on
/// records, the Revit 2024 build produces several hundred errors without this
/// one empty class.
///
/// Compiled ONLY for net48. On .NET the real one exists and a second definition
/// would clash with it.
///
/// Ribbit.Geometry carries its own copy for the same reason. It cannot be shared
/// - the type has to exist in each assembly that uses init setters, and this one
/// is internal so it never leaks into the public surface.
/// </summary>
internal static class IsExternalInit
{
}

#endif
