using System.Runtime.InteropServices;
using System.Windows;

namespace Ribbit.Native.Ui;

/// <summary>
/// The work area of the monitor Revit is ON, in WPF units.
///
/// <see cref="SystemParameters.WorkArea"/> is the PRIMARY monitor only. With
/// Revit on a smaller second screen, a window sized from it opens larger than
/// the display it is centred on and its footer buttons land off-screen.
/// </summary>
internal static class RevitScreen
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// The work area (taskbar excluded) of Revit's monitor in device-
    /// independent units, or the primary one when it cannot be read.
    /// </summary>
    public static Size WorkArea()
    {
        try
        {
            IntPtr revit = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (revit != IntPtr.Zero)
            {
                IntPtr monitor = MonitorFromWindow(revit, MonitorDefaultToNearest);
                MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
                if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                {
                    // Revit is per-monitor DPI aware, so its window reports the
                    // DPI of the monitor it sits on.
                    uint dpi = 96;
                    try { dpi = GetDpiForWindow(revit); } catch (EntryPointNotFoundException) { }
                    double scale = (dpi == 0 ? 96 : dpi) / 96.0;
                    return new Size((info.Work.Right - info.Work.Left) / scale,
                                    (info.Work.Bottom - info.Work.Top) / scale);
                }
            }
        }
        catch (Exception)
        {
            // fall through to the primary monitor
        }

        return new Size(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
    }
}
