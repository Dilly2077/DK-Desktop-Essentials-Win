using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Threading;
using System.Threading.Tasks;
using DKDesktopEssentials.Modules.Controllers.ControllerHub;
using DKDesktopEssentials.Modules.Downloads;
using Microsoft.Web.WebView2.Core;

namespace DKDesktopEssentials;

public partial class MainWindow : Window
{
    private const string AppHost = "dk.local";
    private readonly ControllerHubService _controllerHubService;
    private readonly ControllerHubWebBridge _controllerHubBridge;
    private readonly DownloadCoordinator _downloads;
    private readonly DownloadWebBridge _downloadBridge;
    private readonly string? _webViewDataFolder;
    private readonly CancellationTokenSource _downloadUpdatesStop = new();
    private Task _downloadUpdates = Task.CompletedTask;

    public MainWindow() : this(new DownloadCoordinator(), null) { }

    // The separate local acceptance executable supplies an isolated coordinator
    // and browser profile. Production has no test-mode commands or flags.
    internal MainWindow(DownloadCoordinator downloads, string? webViewDataFolder)
    {
        _controllerHubService = new ControllerHubService();
        _controllerHubBridge = new ControllerHubWebBridge(_controllerHubService);
        _downloads = downloads;
        _webViewDataFolder = webViewDataFolder;
        _downloadBridge = new DownloadWebBridge(_downloads, () =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a download destination", Multiselect = false };
            return System.Threading.Tasks.Task.FromResult(picker.ShowDialog(this) == true ? picker.FolderName : null);
        });

        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // At high scaling the requested minimum can exceed the available desktop.
        // Fit and center the window so the title bar and controls remain reachable.
        var workArea = SystemParameters.WorkArea;
        if (workArea.Width > 0 && workArea.Height > 0)
        {
            MinWidth = Math.Min(MinWidth, workArea.Width);
            MinHeight = Math.Min(MinHeight, workArea.Height);
            Width = Math.Min(Width, workArea.Width);
            Height = Math.Min(Height, workArea.Height);
            Left = workArea.Left + (workArea.Width - Width) / 2;
            Top = workArea.Top + (workArea.Height - Height) / 2;
        }
        try
        {
            var userDataFolder = _webViewDataFolder ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DKDesktopEssentials",
                "WebView2");

            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);

            await WebView.EnsureCoreWebView2Async(environment);

            WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 7, 19, 30);
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
#if DEBUG
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
#else
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif

            var uiFolder = Path.Combine(AppContext.BaseDirectory, "ui");
            if (!Directory.Exists(uiFolder))
            {
                MessageBox.Show(
                    $"The local UI folder could not be found:\n{uiFolder}",
                    "DK Desktop Essentials",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Close();
                return;
            }

            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AppHost,
                uiFolder,
                CoreWebView2HostResourceAccessKind.DenyCors);

            // Only the packaged SPA may navigate or call privileged native APIs.
            WebView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!ControllerHubOriginPolicy.IsTrustedDocumentUri(args.Uri))
                    args.Cancel = true;
            };
            WebView.CoreWebView2.FrameNavigationStarting += (_, args) => args.Cancel = true;
            WebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            WebView.CoreWebView2.WebResourceRequested += (_, args) =>
            {
                // Prevent outbound fetches, remote fonts, iframes and other network resources.
                if (!ControllerHubOriginPolicy.IsTrustedResourceUri(args.Request.Uri))
                    args.Response = WebView.CoreWebView2.Environment.CreateWebResourceResponse(
                        null, 403, "Forbidden", "Content-Type: text/plain");
            };

            WebView.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            WebView.Source = new Uri($"https://{AppHost}/index.html");
            _downloadUpdates = PublishDownloadUpdatesAsync(_downloadUpdatesStop.Token);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                "Microsoft Edge WebView2 Runtime is required to run DK Desktop Essentials. It is included with current Windows 11 installations and most supported Windows 10 systems.",
                "WebView2 Runtime required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"DK Desktop Essentials could not start its local interface.\n\n{ex.Message}",
                "Startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private async Task PublishDownloadUpdatesAsync(CancellationToken token)
    {
        // Bounded local notifications. The adapter requests redacted current views;
        // source URLs, paths, credentials and raw records never enter this event bus.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(750));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (_shutdownStarted) return;
                if (_downloadBridge.UpdatesRequested && WebView.CoreWebView2 is not null &&
                    ControllerHubOriginPolicy.IsTrustedDocumentUri(WebView.Source?.AbsoluteUri))
                    WebView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"downloads:event\",\"reason\":\"refresh\"}");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (InvalidOperationException) { /* The view was closed/crashed; do not publish to another document. */ }
        catch (System.Runtime.InteropServices.COMException) { /* A terminated renderer cannot receive notifications. */ }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // The sender is a security boundary; matching the event type alone is insufficient.
        if (_shutdownStarted || !ControllerHubOriginPolicy.IsTrustedDocumentUri(e.Source))
            return;

        try
        {
        string message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            message = e.WebMessageAsJson;
        }

        // A local page can request privileged operations only after native confirmation.
        if (!ConfirmControllerMaintenance(message, out var deniedResponse))
        {
            if (!_shutdownStarted && WebView.CoreWebView2 is not null && deniedResponse is not null)
                WebView.CoreWebView2.PostWebMessageAsJson(deniedResponse);
            return;
        }

        var downloadResponse = await _downloadBridge.HandleAsync(message, e.Source);
        if (downloadResponse is not null)
        {
            if (!_shutdownStarted && WebView.CoreWebView2 is not null)
                WebView.CoreWebView2.PostWebMessageAsJson(downloadResponse);
            return;
        }

        var controllerResponse = await _controllerHubBridge.HandleAsync(message);
        if (controllerResponse is not null)
        {
            if (!_shutdownStarted && WebView.CoreWebView2 is not null)
                WebView.CoreWebView2.PostWebMessageAsJson(controllerResponse);
            return;
        }

        switch (message)
        {
            case "window:minimize":
                WindowState = WindowState.Minimized;
                break;

            case "window:maximize":
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                break;

            case "window:close":
                Close();
                break;

            case "window:drag":
                try
                {
                    if (WindowState == WindowState.Maximized)
                        WindowState = WindowState.Normal;
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                    // DragMove can fail if the pointer is released before WPF receives the message.
                }
                break;
        }
        }
        catch (Exception ex)
        {
            // An exception from an async-void WebView event otherwise reaches WPF's dispatcher.
            // Do not allow a failed provider/network operation to terminate the app.
            System.Diagnostics.Trace.TraceError(
                $"Unhandled WebView controller command error: {ex.GetType().Name}");
        }
    }

    private bool ConfirmControllerMaintenance(string message, out string? deniedResponse)
    {
        deniedResponse = null;
        if (!message.TrimStart().StartsWith("{", StringComparison.Ordinal))
            return true;

        try
        {
            using var json = JsonDocument.Parse(message);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var commandElement) ||
                commandElement.ValueKind != JsonValueKind.String)
                return true;

            var command = commandElement.GetString();
            var warning = command switch
            {
                "controllerHub:repairOutputBackend" =>
                    "Repairing HIDMaestro can remove virtual controllers created by other applications. Close other affected applications and continue only if you accept that global impact.",
                "controllerHub:removeOutputBackend" =>
                    "Removing HIDMaestro can remove ALL HIDMaestro virtual devices, including ones used by other applications. Continue?",
                "controllerHub:installOutputDriver" =>
                    "This installs an optional Windows virtual-controller driver and may request administrator privileges. HIDMaestro installation also performs global cleanup that can remove other applications' virtual controllers. Close affected applications and proceed only if you accept that impact.",
                _ => null
            };
            if (warning is null)
                return true;

            if (MessageBox.Show(warning, "Confirm controller maintenance",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                return true;

            var requestId = root.TryGetProperty("requestId", out var id) &&
                id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            deniedResponse = JsonSerializer.Serialize(new
            {
                type = "controllerHub:response",
                requestId,
                command,
                ok = false,
                result = (object?)null,
                error = "Controller maintenance was cancelled by the user."
            });
            return false;
        }
        catch (JsonException)
        {
            // The bridge will return a parse/validation error for malformed messages.
            return true;
        }
    }

    private bool _shutdownStarted;
    private bool _shutdownComplete;

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        IsEnabled = false;
        try
        {
            _downloadUpdatesStop.Cancel();
            await _downloadUpdates;
            try { await _downloads.DisposeAsync(); }
            finally { await _controllerHubService.DisposeAsync(); }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Native shutdown: {ex.GetType().Name}"); }
        finally { _downloadUpdatesStop.Dispose(); _shutdownComplete = true; Close(); }
    }
}
