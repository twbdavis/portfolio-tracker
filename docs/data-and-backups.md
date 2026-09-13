# Data and backups

## Storage

The application stores its SQLite database (portfolio.db), settings, and WebView2
profile under **%APPDATA%\PortfolioTracker**. Settings may contain API keys.
Never upload that directory to an issue or public repository.

## Back up

1. Exit the application using its system tray **Exit** command. Closing the window
   alone leaves it running.
2. Copy the entire PortfolioTracker directory to a private backup location. Include
   any SQLite -wal and -shm files if present.
3. Verify the copy exists before restarting the app. Protect backups as private
   financial information and keep more than one dated copy.

## Restore

Exit the app, preserve the current directory under a different name, and copy the
backup back into its original location. Restore the whole database set from the
same backup. Start the same or a newer compatible app version and reconcile the
positions against your brokerage. Schema migrations are forward-only; do not open
a migrated database in an older application build.

## Network use

The local API binds to loopback and uses a per-session token. Market-data requests
go to the configured providers (including Finnhub and supported fallback sources).
Optional AI research can contact a configured Open WebUI server or Anthropic.
Review provider settings before enabling these features. The application stores
keys in a local JSON file; it does not encrypt them separately from your user account.
