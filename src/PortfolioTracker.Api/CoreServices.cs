using PortfolioTracker.Core;
using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Import;
using PortfolioTracker.Core.Portfolio;
using PortfolioTracker.Core.Pricing;
using PortfolioTracker.Core.Research;
using PortfolioTracker.Core.Tax;

namespace PortfolioTracker.Api;

/// One composition of the Core domain over a single SQLite file — the same
/// wiring the WinForms Program.Main used, exposed to endpoints via DI.
public class CoreServices
{
    public required string AppDataDir { get; init; }
    public required Database Db { get; init; }
    public required AppSettings Settings { get; init; }
    public required ResearchCacheRepository ResearchCache { get; init; }
    public required AccountRepository Accounts { get; init; }
    public required TickerTargetRepository TickerTargets { get; init; }
    public required PositionRepository Positions { get; init; }
    public required TransactionRepository Transactions { get; init; }
    public required LotRepository Lots { get; init; }
    public required SaleRepository Sales { get; init; }
    public required CashFlowRepository CashFlows { get; init; }
    public required ImportBatchRepository Batches { get; init; }
    public required PriceCacheRepository PriceCache { get; init; }
    public required WatchlistRepository Watchlist { get; init; }
    public required UniverseRepository Universes { get; init; }
    public required TickerClassificationRepository Classifications { get; init; }
    public required TickerMetricsRepository Metrics { get; init; }
    public required RecommendationSnapshotRepository Recommendations { get; init; }
    public required DailyCloseRepository DailyCloses { get; init; }
    public required SignalHitRepository SignalHits { get; init; }
    public required AppStateRepository AppState { get; init; }
    public required CrawlStampRepository CrawlStamps { get; init; }
    public required DiscoveredTickerRepository Discovered { get; init; }
    public required PortfolioFitRepository PortfolioFit { get; init; }
    public required SignalOutcomeRepository SignalOutcomes { get; init; }
    public required ResearchBriefRepository Briefs { get; init; }
    public required QueueSnoozeRepository QueueSnoozes { get; init; }
    public required AiUsageRepository AiUsage { get; init; }
    public required BriefTraceRepository BriefTraces { get; init; }
    /// Anthropic Messages client for AI research briefs; metered separately
    /// from the Finnhub budget. Kept behind the IBriefModelProvider seam —
    /// AI_VISION_PROVIDER selects it or the local OpenWebUI server.
    public required AnthropicClient Anthropic { get; init; }
    public required OpenWebUiBriefProvider OpenWebUi { get; init; }
    public required BriefPromptTemplates BriefPrompts { get; init; }

    /// The active brief backend per AI_VISION_PROVIDER; resolved per call so
    /// a settings change applies without restart. Null when disabled.
    public IBriefModelProvider? ResolveBriefProvider() => Settings.AiProvider switch
    {
        BriefProviderKinds.OpenWebUi => OpenWebUi,
        BriefProviderKinds.Anthropic => new AnthropicBriefProvider(Anthropic,
            () => string.IsNullOrWhiteSpace(Settings.AnthropicModel)
                ? AnthropicClient.DefaultModel : Settings.AnthropicModel),
        _ => null,
    };
    public required IHistoryProvider History { get; init; }
    /// Yahoo assetProfile business summaries for the brief's company header —
    /// the only keyless source of a real "what the company does" paragraph.
    public required ICompanyDescriptionProvider CompanyDescriptions { get; init; }
    /// Raw providers for the crawler: it manages its own freshness stamps, so
    /// it bypasses the ResearchService cache layer.
    public required IResearchDataProvider ResearchProvider { get; init; }
    public required IPriceProvider PriceProvider { get; init; }
    /// The shared Finnhub token bucket — exposed so the crawler can draw from
    /// the same budget as interactive quote/research calls.
    public required RateGate FinnhubGate { get; init; }
    /// Interactive-use signal; the crawler pauses while this is busy.
    public required InteractiveGate Interactive { get; init; }
    public required ImportService Importer { get; init; }
    public required ManualEntryService ManualEntry { get; init; }
    public required PriceService Prices { get; init; }
    public required ResearchService Research { get; init; }
    public required TaxService Tax { get; init; }
    public required TaxBracketsFile TaxBrackets { get; init; }

    public static CoreServices Create(string appDataDir)
    {
        var settings = AppSettings.Load(appDataDir);
        var db = new Database(Path.Combine(appDataDir, "portfolio.db"));
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // one gate: quote refreshes and research lookups share the free-tier
        // budget (60/min). The burst lets a whole refresh pass fan out at once;
        // the 1.1s refill keeps the sustained rate under the cap.
        var gate = new RateGate(TimeSpan.FromMilliseconds(1100), burst: 50);
        // Finnhub primary; when its feed freezes mid-session (same price and
        // observation on every poll) quotes fail over to Yahoo per ticker
        var priceProvider = new StaleFeedFallbackPriceProvider(
            new FinnhubPriceProvider(http, () => settings.FinnhubApiKey),
            new YahooQuoteProvider(http));
        var researchProvider = new FinnhubResearchProvider(http, () => settings.FinnhubApiKey);
        var services = new CoreServices
        {
            AppDataDir = appDataDir,
            Db = db,
            Settings = settings,
            ResearchCache = new ResearchCacheRepository(db),
            Accounts = new AccountRepository(db),
            TickerTargets = new TickerTargetRepository(db),
            Positions = new PositionRepository(db),
            Transactions = new TransactionRepository(db),
            Lots = new LotRepository(db),
            Sales = new SaleRepository(db),
            CashFlows = new CashFlowRepository(db),
            Batches = new ImportBatchRepository(db),
            PriceCache = new PriceCacheRepository(db),
            Watchlist = new WatchlistRepository(db),
            Universes = new UniverseRepository(db),
            Classifications = new TickerClassificationRepository(db),
            Metrics = new TickerMetricsRepository(db),
            Recommendations = new RecommendationSnapshotRepository(db),
            DailyCloses = new DailyCloseRepository(db),
            SignalHits = new SignalHitRepository(db),
            AppState = new AppStateRepository(db),
            CrawlStamps = new CrawlStampRepository(db),
            Discovered = new DiscoveredTickerRepository(db),
            PortfolioFit = new PortfolioFitRepository(db),
            SignalOutcomes = new SignalOutcomeRepository(db),
            Briefs = new ResearchBriefRepository(db),
            QueueSnoozes = new QueueSnoozeRepository(db),
            AiUsage = new AiUsageRepository(db),
            BriefTraces = new BriefTraceRepository(db),
            // separate client: brief generations legitimately run far past the
            // 15s Finnhub timeout
            Anthropic = new AnthropicClient(new HttpClient { Timeout = TimeSpan.FromSeconds(120) },
                () => settings.AnthropicApiKey),
            // the provider's per-call timeout (from settings, hot-read) is the
            // sole authority; the HttpClient must never pre-empt it, so it has
            // no timeout of its own
            OpenWebUi = new OpenWebUiBriefProvider(new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
                () => (settings.OpenWebUiBaseUrl, settings.OpenWebUiApiKey, settings.OpenWebUiModel),
                () => TimeSpan.FromSeconds(Math.Clamp(settings.OpenWebUiTimeoutSeconds, 10, 3600)),
                () => settings.OpenWebUiDisableThinking),
            BriefPrompts = new BriefPromptTemplates(appDataDir),
            History = new YahooHistoryProvider(http),
            CompanyDescriptions = new YahooProfileProvider(),
            ResearchProvider = researchProvider,
            PriceProvider = priceProvider,
            FinnhubGate = gate,
            Interactive = new InteractiveGate(),
            Importer = new ImportService(db),
            ManualEntry = new ManualEntryService(db),
            Prices = new PriceService(priceProvider, new PriceCacheRepository(db), gate: gate),
            Research = new ResearchService(researchProvider, new ResearchCacheRepository(db), gate),
            Tax = new TaxService(db),
            TaxBrackets = TaxBracketsFile.Load(Path.Combine(appDataDir, "taxbrackets.json")),
        };
        // first run after the tax module was added: replay existing sales into
        // consumption detail + wash adjustments
        if (services.Tax.NeedsBackfill(services.Sales)) services.Tax.RebuildAll();
        UniverseSeeder.EnsureSeeded(services.Universes, services.AppState);
        return services;
    }
}
