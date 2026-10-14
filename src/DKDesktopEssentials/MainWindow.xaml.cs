using System;
using System.IO;
using System.Windows;
using DKDesktopEssentials.Modules.Controllers.ControllerHub;
using Microsoft.Web.WebView2.Core;

namespace DKDesktopEssentials;

public partial class MainWindow : Window
{
    private const string AppHost = "dk.local";
    private readonly ControllerHubService _controllerHubService;
    private readonly ControllerHubWebBridge _controllerHubBridge;

    public MainWindow()
    {
        _controllerHubService = new ControllerHubService();
        _controllerHubBridge = new ControllerHubWebBridge(_controllerHubService);

        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userDataFolder = Path.Combine(
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

    private bool _shutdownStarted;
    private bool _shutdownComplete;

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        IsEnabled = false;
        try { await _controllerHubService.DisposeAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Controller shutdown: {ex.Message}"); }
        finally { _shutdownComplete = true; Close(); }
    }
}
