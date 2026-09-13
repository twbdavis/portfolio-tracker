# Architecture



- `src/PortfolioTracker.Core` — all logic, no UI. `Data/` is the repository
  layer (SQLite via Microsoft.Data.Sqlite; no SQL anywhere else).
  Transactions (`Tx` table) are the source of truth; lots and sale records
  are derived by a pure FIFO replay (`Portfolio/FifoEngine`) whenever
  transactions change, which is what makes batch deletion and
  opening-balance reconciliation safe.
- `Pricing/IPriceProvider` is the market-data swap point: implement
  `Task<PriceQuote?> GetQuoteAsync(string ticker, CancellationToken)` and
  swap the registration in `PortfolioTracker.Api/CoreServices.cs` — nothing
  else references Finnhub. Research data has a sibling swap point,
  `Pricing/IResearchDataProvider`, with the same null-means-unavailable /
  throw-means-broken contract; both providers draw API calls through one
  shared `RateGate`.
- `Sectors/` in Core holds the warehouse analysis layer: `CrawlPlanner` (pure
  TTL-driven work planning), `RobustStats` (median/MAD z-scores),
  `PriceDerived` (returns/SMA/cross state from close series), `SectorAnalyzer`
  (summaries/rankings/heatmap), and `SignalEngine` (the seven opportunity
  rules) — all pure functions over warehouse rows, no I/O, no clock.
- `src/PortfolioTracker.Api` — in-process ASP.NET Core minimal API + SignalR
  hub (`/hub`) that serves the embedded React build from `wwwroot` and pushes
  live quotes/alerts to the UI. Hosted services: `QuotePoller` (live quotes +
  alerts) and `UniverseCrawler` (warehouse refresh + nightly signal
  evaluation). All DB/HTTP access goes through Core.
- `src/PortfolioTracker.Shell` — thin WinForms host: WebView2 window, system
  tray, toast notifications, single-instance mutex, and start-with-Windows
  registration. No domain logic.
- `frontend/` — Vite + React 18 + TypeScript UI (Tailwind, shadcn/ui,
  TanStack Query/Table, Recharts). Built into `src/PortfolioTracker.Api/wwwroot`
  and embedded into the Api assembly on Release publishes.
- Multi-account: `Account` scopes positions, lots, sales, cash flows, and
  import batches; FIFO replays per (account, ticker) while wash-sale
  recomputation runs per ticker across all accounts. Price targets live at
  ticker level (`TickerTarget`). Databases from before multi-account migrate
  automatically (forward-only, `PRAGMA user_version`): existing rows land in
  a placeholder account that the app asks you to describe once, and the first
  import whose rows overlap the old data adopts its real account number.
- `tests/PortfolioTracker.Core.Tests` — xUnit: parser classification, dedupe,
  FIFO consumption incl. splits, oversell, and per-account isolation,
  repositories, calculator (incl. cumulative blended aggregation), market
  hours, alert rules, cross-account and IRA wash flavors, the v0→v1
  migration against a seeded pre-migration database, and the shipped
  two-account sample CSV end-to-end.
- `tests/PortfolioTracker.Api.Tests` — xUnit integration tests against a live
  in-process API host (endpoints, auth token, SignalR quote pushes).
  `frontend/` has a Vitest suite for the formatting and live-quote merge
  utilities (`npx vitest run`).
