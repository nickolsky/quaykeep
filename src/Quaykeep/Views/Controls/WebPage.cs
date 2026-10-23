using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using NativeWebView.Core;
using Quaykeep.Core;
using WebView = NativeWebView.Controls.NativeWebView;

namespace Quaykeep.Views.Controls;

/// <summary>
/// One of the app's own pages (Assets\terminal, Assets\editor) in the system web engine through NativeWebView:
/// WebView2 on Windows, WKWebView on macOS, WebKitGTK on Linux. The page is loaded from the app folder as a file:// URL
/// and may not navigate anywhere else or open windows; it talks to the app with window.chrome.webview messages
/// (JSON both ways). All pages share one browser profile next to the data folder (<see cref="AppPaths.WebViewDir"/>).
/// When the engine is missing (no WebView2 Runtime, no WebKitGTK) the page shows Term.WebViewMissing instead.
/// </summary>
public sealed class WebPage : Border, IDisposable
{
    private static string? _platformProblem;
    private static bool _platformChecked;

    private readonly string _folder;
    private readonly string _page;
    private readonly bool _contextMenu;
    private WebView? _web;
    private bool _started;
    private bool _disposed;

    /// <param name="folder">Folder under Assets (terminal, editor).</param>
    /// <param name="page">Page in it (terminal.html…).</param>
    /// <param name="contextMenu">Keep the engine's own context menu (the editor's copy / paste); the terminal pages draw their own.</param>
    public WebPage(string folder, string page, bool contextMenu)
    {
        _folder = Path.Combine(AppContext.BaseDirectory, "Assets", folder);
        _page = page;
        _contextMenu = contextMenu;
        Background = Brushes.Transparent;
        AttachedToVisualTree += (_, _) => Start();
    }

    /// <summary>A message from the page (on the UI thread).</summary>
    public event Action<JsonElement>? MessageReceived;

    /// <summary>The web engine could not start; the page shows why.</summary>
    public event Action<string>? Failed;

    /// <summary>Null when this OS has a usable web engine, else what is missing.</summary>
    public static string? PlatformProblem
    {
        get
        {
            if (_platformChecked) return _platformProblem;
            _platformChecked = true;
            try
            {
                NativeWebViewRuntime.EnsureCurrentPlatformRegistered();
                var status = NativeWebViewPlatformImplementationStatusMatrix.Get(NativeWebViewRuntime.CurrentPlatform);
                if (status.EmbeddedControl != NativeWebViewRepositoryImplementationStatus.RuntimeImplemented)
                    return _platformProblem = status.Summary;
                var diagnostics = NativeWebViewRuntime.GetCurrentPlatformDiagnostics();
                if (!diagnostics.IsReady)
                    _platformProblem = string.Join("\n", diagnostics.Issues.Where(i => i.Severity == NativeWebViewDiagnosticSeverity.Error)
                        .Select(i => string.IsNullOrEmpty(i.Recommendation) ? i.Message : $"{i.Message} {i.Recommendation}"));
                if (!diagnostics.IsReady && string.IsNullOrWhiteSpace(_platformProblem)) _platformProblem = diagnostics.ProviderName;
            }
            catch (Exception ex)
            {
                _platformProblem = ex.Message;
            }
            return _platformProblem;
        }
    }

    private async void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        if (PlatformProblem is { } problem)
        {
            Fail(problem);
            return;
        }
        WebView web;
        try
        {
            web = new WebView();
            var config = web.InstanceConfiguration;
            config.EnvironmentOptions.UserDataFolder = AppPaths.WebViewDir;
            config.EnvironmentOptions.CacheFolder = Path.Combine(AppPaths.WebViewDir, "cache");
            config.ControllerOptions.ProfileName = "quaykeep" + AppPaths.InstanceSuffix;
            config.ControllerOptions.IsPasswordAutosaveEnabled = false;
            config.ControllerOptions.IsGeneralAutofillEnabled = false;
            web.NavigationStarted += OnNavigationStarted;
            web.NewWindowRequested += (_, e) => e.Handled = true;
            web.WebMessageReceived += OnWebMessage;
            web.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess && e.Error == "browser-process-failed") Dispatcher.UIThread.Post(() => Fail(e.Error));
            };
            _web = web;
            Child = web;
            await web.InitializeAsync();
            if (_disposed) return;
            Toggle(() => web.IsDevToolsEnabled = Debugger.IsAttached);
            Toggle(() => web.IsContextMenuEnabled = _contextMenu);
            Toggle(() => web.IsStatusBarEnabled = false);
            Toggle(() => web.IsZoomControlEnabled = false);
            web.Navigate(new Uri(Path.Combine(_folder, _page)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(ex.Message);
        }
    }

    /// <summary>A setting this engine does not support stays at its default.</summary>
    private static void Toggle(Action set)
    {
        try
        {
            set();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or InvalidOperationException)
        {
        }
    }

    private void Fail(string reason)
    {
        if (_disposed) return;
        var web = _web;
        _web = null;
        Child = new TextBlock { Text = L.F("Term.WebViewMissing", reason), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) };
        web?.Dispose();
        Failed?.Invoke(reason);
    }

    /// <summary>Only the page's own folder: links open in the browser through the page's "link" message, not here.</summary>
    private void OnNavigationStarted(object? sender, NativeWebViewNavigationStartedEventArgs e)
    {
        if (e.Uri is not { } uri || uri.Scheme == "about") return;
        if (!uri.IsFile) e.Cancel = true;
        else
        {
            var path = Path.GetFullPath(uri.LocalPath);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_folder)) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, AppPaths.PathComparison)) e.Cancel = true;
        }
    }

    private void OnWebMessage(object? sender, NativeWebViewMessageReceivedEventArgs e)
    {
        var json = string.IsNullOrEmpty(e.Json) ? e.Message : e.Json;
        if (string.IsNullOrEmpty(json)) return;
        JsonElement message;
        try
        {
            message = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return;
        }
        // a page that posts a string of JSON instead of an object
        if (message.ValueKind == JsonValueKind.String)
        {
            try
            {
                message = JsonDocument.Parse(message.GetString() ?? "").RootElement;
            }
            catch (JsonException)
            {
                return;
            }
        }
        if (message.ValueKind != JsonValueKind.Object) return;
        if (Dispatcher.UIThread.CheckAccess()) Raise(message);
        else Dispatcher.UIThread.Post(() => Raise(message));
    }

    private void Raise(JsonElement message)
    {
        if (!_disposed) MessageReceived?.Invoke(message);
    }

    /// <summary>Sends a message to the page (its "message" listeners get it as an object); dropped while the page is not there.</summary>
    public void Post(object message)
    {
        if (_disposed || _web is not { IsInitialized: true } web) return;
        try
        {
            web.PostWebMessageAsJsonAsync(JsonSerializer.Serialize(message), CancellationToken.None)
                .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or PlatformNotSupportedException)
        {
        }
    }

    /// <summary>Gives the page the keyboard.</summary>
    public void FocusPage()
    {
        if (_web is not { } web) return;
        web.Focus();
        Toggle(() => web.MoveFocus(NativeWebViewFocusMoveDirection.Programmatic));
    }

    /// <summary>The app's background and whether it is dark, for the page's palette.</summary>
    public static (bool Dark, string Background) PageTheme(Control control)
    {
        var bg = control.TryFindResource("SolidBackgroundFillColorQuarternaryBrush", control.ActualThemeVariant, out var v) && v is ISolidColorBrush b
            ? b.Color : Colors.Black;
        var dark = 0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B < 128;
        return (dark, $"#{bg.R:x2}{bg.G:x2}{bg.B:x2}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _web?.Dispose();
        _web = null;
    }
}
