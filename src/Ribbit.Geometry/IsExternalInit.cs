namespace System.Runtime.CompilerServices;

/// <summary>
/// Compiler shim for `init` accessors (and therefore records) on
/// netstandard2.0, which predates this type. The compiler only needs the name
/// to exist — .NET 5+ supplies its own, so this is never used at runtime there.
///
/// Required because this assembly deliberately targets netstandard2.0 so it can
/// load under both .NET Framework 4.8 (Revit 2024) and .NET 8 (Revit 2025+).
/// </summary>
internal static class IsExternalInit
{
}
