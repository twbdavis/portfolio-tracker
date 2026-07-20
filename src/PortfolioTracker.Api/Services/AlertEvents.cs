using PortfolioTracker.Api.Hubs;

namespace PortfolioTracker.Api.Services;

/// In-process alert fan-out: the shell subscribes to fire Windows toasts while
/// the same alert goes to the frontend over SignalR.
public class AlertEvents
{
    public event Action<AlertPush>? AlertFired;
    internal void Raise(AlertPush alert) => AlertFired?.Invoke(alert);
}
