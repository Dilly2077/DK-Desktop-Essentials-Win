using System;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace DKDesktopEssentials;

public partial class MainWindow : Window
{
    private const string AppHost = "dk.local";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
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

            WebView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) ||
                    !string.Equals(target.Host, AppHost, StringComparison.OrdinalIgnoreCase))
                {
                    args.Cancel = true;
                }
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

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var message = e.TryGetWebMessageAsString();

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
}
