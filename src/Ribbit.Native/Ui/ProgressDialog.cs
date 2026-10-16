using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Ribbit.Geometry;

namespace Ribbit.Native.Ui;

/// <summary>
/// Replacement for pyRevit's forms.ProgressBar, which has no Revit equivalent.
///
/// Each tee goes through the pipe type's routing preferences, which is the slow
/// part: on a big fire-protection model a single routing-preference tap can take
/// a very long time. Without a progress bar a long run looks like a hung Revit
/// with no way out.
///
/// The build runs on Revit's main thread, so the window cannot repaint itself —
/// <see cref="Update"/> pumps the dispatcher explicitly to keep it live and to
/// let the Cancel button register.
/// </summary>
internal sealed class ProgressDialog : IDisposable
{
    private readonly Window _window;
    private readonly ProgressBar _bar;
    private readonly TextBlock _label;
    private readonly string _titleFormat;

    private int _lastReported = -1;
    private int _lastMax = -1;

    public bool Cancelled { get; private set; }

    /// <param name="titleFormat">
    /// Uses {0} for the current value and {1} for the maximum, mirroring
    /// pyRevit's "{value} of {max_value}".
    /// </param>
    public ProgressDialog(string titleFormat, bool cancellable)
    {
        _titleFormat = titleFormat;

        _bar = new ProgressBar { Height = 20, Minimum = 0, Maximum = 1 };
        _label = new TextBlock { Margin = new Thickness(0, 0, 0, 8) };

        Button cancel = new()
        {
            Content = "Cancel",
            Width = 90,
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            IsEnabled = cancellable,
        };
        cancel.Click += (_, _) =>
        {
            Cancelled = true;
            cancel.IsEnabled = false;
            cancel.Content = "Cancelling...";
        };

        StackPanel panel = new() { Margin = new Thickness(16) };
        panel.Children.Add(_label);
        panel.Children.Add(_bar);
        panel.Children.Add(cancel);

        _window = new Window
        {
            Title = "Ribbit",
            Content = panel,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
        };
        new WindowInteropHelper(_window).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        if (RibbitUi.TestOwner is not null)
            _window.Owner = RibbitUi.TestOwner;   // tests: so the suite can press its Cancel

        _window.Show();
        Pump();

        if (TestCancel?.Invoke(titleFormat) == true)
            Cancelled = true;   // tests: exactly what the Cancel button sets
    }

    /// <summary>
    /// Tests only: given the title format, true = behave as if Cancel was pressed
    /// the moment the window opened. The button sets the same flag; a test cannot
    /// click it in time, because the window only pumps while the run updates it.
    /// </summary>
    internal static Func<string, bool>? TestCancel { get; set; }

    /// <summary>
    /// Reports progress, repainting at most ~100 times over the whole run.
    ///
    /// THE THROTTLE IS LOAD-BEARING, not a nicety. <see cref="Pump"/> forces the
    /// dispatcher to drain and lay out on every call, so an unthrottled caller
    /// pays a full UI round-trip PER ELEMENT — on a large model that is hundreds
    /// of thousands of them, and it can cost more than the work being measured.
    /// The clash engine explicitly documents throttling as the caller's job;
    /// keeping it here rather than in each caller means no call site can forget
    /// it, which is exactly what happened when it lived in the callers.
    ///
    /// Callers pass every item; <see cref="ProgressThrottle"/> decides what
    /// actually reaches the screen.
    /// </summary>
    public void Update(int value, int max)
    {
        // A new phase (different total) starts fresh, so the bar cannot sit
        // showing the previous phase's position.
        if (max != _lastMax)
        {
            _lastMax = max;
            _lastReported = -1;
        }

        if (!ProgressThrottle.ShouldReport(value, max, _lastReported))
            return;

        _lastReported = value;

        _bar.Maximum = Math.Max(1, max);
        _bar.Value = value;
        _label.Text = string.Format(_titleFormat, value, max);
        Pump();
    }

    /// <summary>
    /// Sets the label directly, for a phase with no item counter of its own — a
    /// single long step that would otherwise sit behind a blank window. Leaves
    /// the bar where it is.
    /// </summary>
    public void Message(string text)
    {
        _label.Text = text;
        Pump();
    }

    /// <summary>
    /// Lets the window paint and the Cancel click arrive. Without this the
    /// dialog would be a frozen rectangle for the whole build, since nothing
    /// yields back to the message loop.
    /// </summary>
    private static void Pump()
        => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    public void Dispose() => _window.Close();
}
