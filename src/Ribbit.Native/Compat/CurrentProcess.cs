namespace Ribbit.Native.Compat;

/// <summary>
/// This process's id on both frameworks. .NET 8 has the cheap
/// <c>Environment.ProcessId</c> (and the analyzers insist on it); .NET Framework
/// 4.8, the Revit 2024 build, does not have it at all.
/// </summary>
internal static class CurrentProcess
{
#if NETFRAMEWORK
    public static int Id => System.Diagnostics.Process.GetCurrentProcess().Id;
#else
    public static int Id => Environment.ProcessId;
#endif
}
