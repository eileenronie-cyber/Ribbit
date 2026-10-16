using System.IO;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Ribbit.Native.Ui;

/// <summary>
/// Modeless HTML report window — the replacement for pyRevit's
/// script.get_output().
///
/// Deliberately modeless, like the pyRevit original: these are dry-run tools,
/// and the report has to stay readable while the user works the model and
/// answers the confirmation. Element links go through
/// <see cref="RevitLinks"/> rather than being handled here, because a modeless
/// window has no Revit API context of its own.
/// </summary>
internal sealed class ReportWindow : Window
{
    private readonly WebView2 _web = new();
    private readonly string _html;
    private readonly Func<string, string?>? _onMessage;

    /// <param name="onMessage">
    /// Optional handler for JSON messages the page posts back — what lets a
    /// report DO something rather than only display. Returns a JSON reply, or
    /// null for no reply.
    ///
    /// Element links keep using navigation (see <see cref="OnNavigationStarting"/>)
    /// rather than moving here: every other report in the package already emits
    /// those, and this is additive so none of them change.
    /// </param>
    public ReportWindow(string title, string html, Func<string, string?>? onMessage = null)
    {
        Title = title;
        _html = html;
        _onMessage = onMessage;
        Width = 1000;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Owned by Revit's main window so it floats above it and closes with it,
        // rather than becoming an orphan in the taskbar.
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        Content = _web;
        Loaded += OnLoadedAsync;
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            // Revit's own process directory is not writable for WebView2's user
            // data, so pin it somewhere that always is.
            string userData = Path.Combine(Path.GetTempPath(), "Ribbit.WebView2");
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: userData);

            await _web.EnsureCoreWebView2Async(env);

            CoreWebView2Settings settings = _web.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = true;

            _web.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _web.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;

            if (_onMessage is not null)
            {
                settings.IsWebMessageEnabled = true;
                _web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            }

            _web.CoreWebView2.NavigateToString(_html);
        }
        catch (Exception ex)
        {
            // WebView2 runtime missing or blocked: say so plainly rather than
            // leaving a blank window, and don't take the tool down with it.
            Content = new System.Windows.Controls.TextBox
            {
                Text = "The report could not be displayed because the WebView2 runtime is "
                       + $"unavailable:\n\n{ex.Message}\n\nInstall the Microsoft Edge WebView2 "
                       + "Runtime to see formatted reports.",
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16),
                BorderThickness = new Thickness(0),
            };
        }
    }

    /// <summary>
    /// Runs the page's request and posts the reply back.
    ///
    /// Fires on the UI thread, which is what makes a clipboard write here legal.
    /// It is NOT a Revit API context, though — a handler that needs the document
    /// must marshal in through an ExternalEvent the way <see cref="RevitLinks"/>
    /// does. The clash handler only touches CSV and the clipboard, so it does
    /// not need to.
    /// </summary>
    private void OnWebMessageReceived(
        object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? reply;
        try
        {
            reply = _onMessage!(e.TryGetWebMessageAsString());
        }
        catch (Exception ex)
        {
            // A failed action must leave the report usable rather than killing
            // the window - the page shows whatever this says.
            reply = "{\"ok\":false,\"message\":"
                    + System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}";
        }

        if (reply is null)
            return;

        try
        {
            _web.CoreWebView2.PostWebMessageAsString(reply);
        }
        catch (Exception)
        {
            // The page closed between the request and the reply.
        }
    }

    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Element links are ours to act on, never to navigate to. Everything
        // else (there should be nothing) is blocked too - this window only ever
        // shows report HTML we generated.
        if (RevitLinks.TryHandle(e.Uri))
        {
            e.Cancel = true;
            return;
        }

        if (!e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            e.Cancel = true;
    }
}
