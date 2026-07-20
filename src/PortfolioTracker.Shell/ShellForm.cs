using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PortfolioTracker.Api;
using System.Text.Json;

namespace PortfolioTracker.Shell;

/// A borderless-content window that is nothing but WebView2: the entire UI is
/// the React app. The shell owns the tray icon, minimize-to-tray-on-close,
/// Windows toasts for alerts, single-instance focus, and window sizing.
public class ShellForm : Form
{
    private readonly ApiHost _host;
    private readonly string _startUrl;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private NotifyIcon? _tray;
    private bool _reallyExit;
    private bool _userResized;
    private bool _applyingAutoSize;

    public ShellForm(ApiHost host, string startUrl, EventWaitHandle showSignal, bool startMinimized)
    {
        _host = host;
        _startUrl = startUrl;
        Text = "PortfolioTracker";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* dev builds may lack an icon */ }
        StartPosition = FormStartPosition.CenterScreen;

        // launch at 90% of the working area; the height bridge grows from there
        var work = Screen.PrimaryScreen!.WorkingArea;
        Width = (int)(work.Width * 0.9);
        Height = (int)(work.Height * 0.9);

        if (startMinimized)
        {
            Opacity = 0;
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Shown += (_, _) =>
            {
                Hide();
                WindowState = FormWindowState.Normal;
                ShowInTaskbar = true;
                Opacity = 1;
            };
        }

        Controls.Add(_webView);
        ResizeEnd += (_, _) => { if (!_applyingAutoSize) _userResized = true; };
        InitializeTray();
        WatchShowSignal(showSignal);
        _host.Alerts.AlertFired += OnAlertFired;
        Load += async (_, _) => await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        // WebView2 user data lives next to the db, not in Program Files
        var env = await CoreWebView2Environment.CreateAsync(
            userDataFolder: Path.Combine(PortfolioTracker.Core.AppSettings.AppDataDir, "WebView2"));
        await _webView.EnsureCoreWebView2Async(env);
        var wv = _webView.CoreWebView2;
        wv.Settings.AreDefaultContextMenusEnabled = false;
        wv.Settings.IsStatusBarEnabled = false;
#if !DEBUG
        wv.Settings.AreDevToolsEnabled = false;
#endif
        // the frontend reports its ideal content height after the dashboard renders
        wv.WebMessageReceived += OnWebMessage;
        wv.Navigate(_startUrl);
    }

    /// Height bridge: grow-only, clamped to the working area, and never fights
    /// a manual resize made this session.
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var msg = JsonDocument.Parse(e.WebMessageAsJson).RootElement;
            if (msg.GetProperty("type").GetString() != "contentHeight") return;
            var contentHeight = msg.GetProperty("height").GetInt32();
            if (_userResized || WindowState != FormWindowState.Normal) return;

            var chromeH = Height - ClientSize.Height;
            var required = chromeH + contentHeight;
            var work = Screen.FromControl(this).WorkingArea;
            var newHeight = Math.Min(Math.Max(Height, required), work.Height);
            if (newHeight <= Height) return;   // grow-only
            _applyingAutoSize = true;
            try
            {
                Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - newHeight));
                Height = newHeight;
            }
            finally { _applyingAutoSize = false; }
        }
        catch (Exception) { /* malformed bridge message: ignore */ }
    }

    private void InitializeTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _reallyExit = true; Close(); });
        _tray = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = "PortfolioTracker - alerts active while running",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        FormClosing += (_, e) =>
        {
            if (_reallyExit || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;   // closing minimizes to tray so alerts keep working
            Hide();
        };
        FormClosed += (_, _) =>
        {
            if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
            ToastNotificationManagerCompat.Uninstall();
        };
        // toast click restores the window
        ToastNotificationManagerCompat.OnActivated += _ =>
        {
            if (IsHandleCreated) BeginInvoke(RestoreFromTray);
        };
    }

    private void WatchShowSignal(EventWaitHandle showSignal)
    {
        var watcher = new Thread(() =>
        {
            while (showSignal.WaitOne())
            {
                if (IsDisposed) return;
                try { BeginInvoke(RestoreFromTray); } catch (ObjectDisposedException) { return; }
            }
        }) { IsBackground = true };
        watcher.Start();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnAlertFired(PortfolioTracker.Api.Hubs.AlertPush alert)
    {
        new ToastContentBuilder()
            .AddText(alert.Message)
            .AddText($"PortfolioTracker {(alert.Kind == "watchlist" ? "watchlist" : "price")} alert")
            .Show();
    }
}
