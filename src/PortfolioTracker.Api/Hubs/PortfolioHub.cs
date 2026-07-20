using Microsoft.AspNetCore.SignalR;

namespace PortfolioTracker.Api.Hubs;

/// Server→client only. Events: "quotes" (batched price pushes — the frontend
/// never polls prices), "alertFired", "importProgress".
public class PortfolioHub : Hub;

public record QuotePush(string Ticker, decimal Price, decimal? PrevClose, DateTimeOffset FetchedAt);

public record AlertPush(string Kind, string Ticker, decimal Price, decimal TargetPrice, string Message);
