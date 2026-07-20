using PortfolioTracker.Api;
using PortfolioTracker.Core;

namespace PortfolioTracker.Shell;

internal static class Program
{
    private const string SingleInstanceMutex = "PortfolioTracker.Shell.SingleInstance";
    private const string ShowSignal = "PortfolioTracker.Shell.Show";

    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out var isFirst);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignal);
        if (!isFirst)
        {
            showSignal.Set();   // second launch: focus the first instance and quit
            return;
        }

        ApplicationConfiguration.Initialize();
        var startMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        var token = Guid.NewGuid().ToString("N");
        var host = new ApiHost();
        try
        {
            host.StartAsync(new ApiHostOptions
            {
                AppDataDir = AppSettings.AppDataDir,
                Token = token,
                DevCorsOrigin = Environment.GetEnvironmentVariable("PT_DEV_CORS_ORIGIN"),
            }).GetAwaiter().GetResult();

            // dev: point the shell (or a plain browser) at the Vite dev server
            var devUrl = Environment.GetEnvironmentVariable("PT_FRONTEND_DEV_URL");
            var target = devUrl is null
                ? $"{host.BaseUri}?token={token}"
                : $"{devUrl}?token={token}&apiBase={Uri.EscapeDataString(host.BaseUri.ToString())}";

            Application.Run(new ShellForm(host, target, showSignal, startMinimized));
        }
        finally
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
