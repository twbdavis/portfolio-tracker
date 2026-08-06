# PortfolioTracker

Local Windows desktop app that tracks long-only stock/ETF positions, compares
live prices against your cost basis, and fires Windows toast notifications
when a position crosses a target price. Fully local: single-user, no cloud
backend, no login. Data lives in SQLite at
`%APPDATA%\PortfolioTracker\portfolio.db`.

The UI is a React app rendered in WebView2 inside a thin WinForms shell
(`PortfolioTracker.Shell`), served by an in-process ASP.NET Core API bound to
127.0.0.1 on a random port and locked with a per-session bearer token. All
domain logic (FIFO, wash sales, importer, tax math) lives in
`PortfolioTracker.Core`.

## Setup

1. Install the .NET 8 (or later) SDK and Node.js 20+.
2. `cd frontend && npm ci && npm run build` (embeds automatically on Release
   publishes; Debug runs serve whatever was last built into
   `src/PortfolioTracker.Api/wwwroot` — copy `frontend/dist/*` there after
   building, or use the dev workflow below).
3. `dotnet run --project src/PortfolioTracker.Shell`
4. The app starts with an empty database.

## Development workflow (two terminals)

Frontend changes with HMR, without rebuilding the shell:

1. Terminal 1 — `cd frontend && npm run dev` (Vite on <http://localhost:5173>).
2. Terminal 2 — set the two env vars and launch the shell:

   ```powershell
   $env:PT_FRONTEND_DEV_URL = "http://localhost:5173"
   $env:PT_DEV_CORS_ORIGIN  = "http://localhost:5173"
   dotnet run --project src/PortfolioTracker.Shell
   ```

   The shell starts the API as usual, then points WebView2 at the Vite dev
   server, passing `?token=…&apiBase=…` so the app can reach the real API.
   You can also open the same URL (with the query string the shell logs) in a
   plain browser — CORS for the Vite origin is enabled when
   `PT_DEV_CORS_ORIGIN` is set.

Release publish (self-contained single folder, frontend built and embedded by
MSBuild):

```powershell
dotnet publish src/PortfolioTracker.Shell -c Release -r win-x64 --self-contained
```

## Getting a Finnhub API key

Quotes come from Finnhub's free tier (60 calls/min). Register at
<https://finnhub.io/register>, copy your API key, and paste it into
**Settings** in the app toolbar. The key is stored locally in
`%APPDATA%\PortfolioTracker\appsettings.json` (git-ignored).

## Exporting transactions from Chase

Chase → Investments → **Transactions** → set a date filter →
**"Things you can do"** → **CSV**. Chase caps each export at one year, so
export year-by-year for longer histories. Overlapping date ranges are fine —
the importer detects duplicate rows (matched on Account Number, Trade Date,
Type, Ticker, Quantity, Amount USD) and skips them. Chase splits some
liquidations into a whole-share row plus a fractional-remainder row (same
date, same price); those are legitimate separate rows and are never merged.
Cost basis always comes from **Amount USD** — the Price USD column is rounded
to two decimals and display-only — and sell proceeds are Amount USD too,
which is NET of regulatory fees (the tax-correct number). Then use **Import
Chase CSV** in the toolbar. Bank transfers (BNK) and cash dividends are
captured as account cash flows — they feed the Deposited / Cash (sweep) /
Dividends / Account Total row above the totals. Money-market sweep rows are
internal moves and are skipped by their Security Type (sweep tickers vary by
account: QACDS in brokerage, QDERQ in IRAs); any unrecognized transaction
type is listed in the import summary so new codes don't pass silently. If a
file's headers don't match the expected Chase format, a column-mapping dialog
opens instead of failing.

**Multiple accounts.** One export can contain several accounts (e.g. a
taxable brokerage and a Roth IRA); every row routes by its Account Number.
The first time a new account number appears, the import pauses with a
new-account dialog asking for a display name and tax treatment — pre-filled
as Roth IRA when the account name contains "Ret", the sweep rows say
"CHASE IRA DEPOSIT SWEEP", or deposits reference "IRA:". Each account gets
its own import batch, so deleting a batch never crosses accounts. The main
window shows one tab per account plus an **All accounts** tab (the default)
that aggregates one row per ticker with a blended average cost; a ◆ glyph
marks tickers also held in an IRA.

After every import a reconciliation summary shows the resulting share count
per ticker per account — eyeball it against Chase's positions page. A sample
two-account file in the exact Chase format (taxable + Roth IRA, both sweep
tickers, a split whole+fractional sell, a rounded-price fractional buy) ships
at `samples/chase-transactions-sample.csv`.

## FIFO and opening balances

Sells consume lots oldest-first (FIFO), splitting a lot when partially
consumed; realized gain/loss per sale is proceeds minus the FIFO cost basis
(see the **Realized P/L** view). FIFO consumption stays strictly within one
account — a sale never consumes another account's lots. If a sell exceeds the shares on file —
typically because your CSV history starts after your first purchase — the
import does not fail: the position is flagged **needs opening balance** (⚠ in
the grid) and you are prompted to enter an opening lot (shares + cost basis
as of a date). Once entered, everything is recomputed from scratch, so the
share counts and realized numbers line up with Chase. Deleting an import
batch (toolbar → Import Batches) likewise removes its transactions and
rebuilds the affected positions.

## Managing positions manually

Positions don't have to come from a CSV. **Add Position** on the toolbar
creates a ticker with an optional first lot and target price. **Edit
Position** (or double-click a row, or right-click → Edit) opens the detail
view where you can add/edit/delete manual lots and change the target, alert,
and notes — lots that came from a Chase import stay read-only there and
change only via their import batch. **Delete Position** (also on the
right-click menu) removes the stock entirely: its transactions, lots, sales
history, and cached price. If the position included imported rows, their
dedupe keys are deleted with them, so re-importing the CSV brings them back.

## Alerts

One target price per **ticker** — regardless of how many accounts hold it —
at-or-above (default) or at-or-below for buy-the-dip watches. Quote polling
is likewise per ticker, never per row or per tab. When a fresh quote crosses the target the app fires a
Windows toast and marks the alert TRIGGERED; it will not fire again until you
re-arm it (position detail → Re-arm Alert) or change the target. Closing the
window minimizes to the system tray so alerts keep working; use the tray
menu's **Exit** to really quit. **The app must be running for alerts to
fire** — there is no background service in v1. To cover reboots, enable
**Settings → Start with Windows (minimized to tray)**: it registers a
per-user Run key that launches the app with `--minimized`, so it boots
straight to the tray at login and polling starts immediately. Toggling the
setting rewrites the registered path, which also heals it after the exe
moves.

## Research and watchlist

**Research** on the toolbar (or right-click a position → "Research this
ticker") opens a deep-dive view for evaluating a candidate before buying:
search by ticker or company name, then the form loads a snapshot (price, day
change, market cap, 52-week range bar), fundamentals grouped by valuation /
growth / profitability / risk, six months of analyst recommendation trends,
the last four quarters of EPS actual-vs-estimate with the next earnings date,
and 30 days of headlines. Each panel loads independently — a slow or
unavailable endpoint grays out that panel only.

Data comes from Finnhub free-tier endpoints: `/search`, `/quote`,
`/stock/profile2`, `/stock/metric`, `/stock/recommendation`,
`/stock/earnings`, `/calendar/earnings`, and `/company-news`. Slow-moving
data (profile, fundamentals, recommendations, earnings) is cached in SQLite
for 24 hours; news for 1 hour; quotes are always live. All calls share the
same 60-calls/min budget as price refreshes. **ETFs will show sparse
fundamentals** — most valuation/growth metrics simply don't exist for them,
so "—" and "not available" notes there are expected, not errors.

**Compare** collects up to three tickers into a side-by-side table (metrics
as rows) and subtly highlights the best value per row — lowest P/E and P/S,
highest growth/margins/ROE. Deliberately no composite score: the app
presents data, you make the call.

**Watchlist** (second tab) saves a candidate with a written thesis,
conviction (1–5), and an optional target buy price. Watchlist targets are
polled by the same loop as position alerts and fire the same one-shot toast
when price drops to the target ("GLXY dropped to $24.10 — your buy target
was $25.00"); re-arm from the watchlist tab. **I Bought It** records the lot
through the normal manual-entry flow, copies the thesis into the new
position's notes, and offers to retire the watchlist entry. Watchlist items
never count toward portfolio totals.

## Sector analysis, universes, and the metrics warehouse

The **Sectors** page ranks tickers against their sector and the **Signals**
page flags rule-based "worth a look" candidates. Both are only feasible on
Finnhub's free tier (60 calls/min, no screener, no historical candles) because
of one architectural rule: **analysis queries never call the network**. A
background crawler warehouses everything into SQLite; the pages read SQLite.

**Universes** bound what gets crawled. Five seed universes ship in the repo
(S&P 500, Semiconductors & Memory, Mega-cap Tech, Defense & Aerospace, Broad
ETFs — `src/PortfolioTracker.Core/Seeds/universes.json`) and load once per
database; they're a point-in-time snapshot (early 2026), fully editable, and a
deleted seed stays deleted. Add tickers by hand, from holdings, from the
watchlist, or from any crawled sector (Sectors page → Universes).

**The crawler** (`UniverseCrawler`) sweeps every ticker in any universe and
refreshes whatever has passed its TTL: company profile (sector classification,
7 days), fundamentals metrics (24 h), analyst recommendation snapshots (24 h),
a daily quote for tickers not already live-polled (24 h), and daily-close
history. It runs after the 4:30 PM ET close on weekdays, on demand ("Refresh
data now"), and resumes an interrupted pass on restart (freshness stamps make
the work plan self-healing). Budget: at most `CrawlerCallsPerMinute` of the 60
Finnhub calls/min (default 30, editable in `%APPDATA%\PortfolioTracker\
appsettings.json`), drawn through the same token bucket as interactive calls —
and any interactive research or quote request pauses the crawler entirely
until a few seconds after it finishes. Sector = Finnhub's `finnhubIndustry`;
you can override any ticker's sector via `PUT /api/classification/{ticker}`
and the override survives future crawls.

**Historical prices** come from Yahoo Finance's chart API
(`query1.finance.yahoo.com/v8/finance/chart`) — free, no API key, so they
don't touch the Finnhub budget. (The original source, Stooq, now fronts its
CSV downloads with a JavaScript bot challenge; `StooqHistoryProvider` remains
in the tree as the documented `IHistoryProvider` swap point.) The Yahoo
endpoint is unofficial-but-long-stable; the parser is deliberately strict, so
a format change or bot wall surfaces as a crawl error instead of silently
stamping tickers as "no history". Two years of split-adjusted closes are
backfilled once per ticker, then the app snapshots its own closes from live
quotes going forward (official history closes replace same-day live
approximations, never the reverse). Tickers the source doesn't know simply
have no momentum/SMA data — those columns and signals stay blank ("—"),
everything else works. Derived values (returns, SMA50/200, cross state, % off
52-week high) require minimum bar counts and are null rather than approximate
below them.

**Relative-value math** uses median and MAD (median absolute deviation), not
mean/stdev — sector populations are small and one outlier would poison every
z-score in the sector. Sectors with fewer than 3 usable values, or zero
spread, produce null z-scores rather than fake ones. ETFs legitimately lack
fundamentals, so ETF-heavy sectors show sparse rankings by design.

**Opportunity signals** are transparent rules — no composite score, no ML, and
deliberately no buy/sell language. Shipped kinds: QualityOnSale, CheapGrowth,
RecMomentum, GoldenCross/DeathCross, SectorDivergence, CatalystWatch (uses
locally cached earnings from tickers you've researched), and the sector-level
BreadthThrust. Every firing stores the exact observed values and thresholds;
the feed's "why this fired" panel replays that arithmetic. Each hit also
carries a **strength** score (0-100): the average margin by which it cleared
its thresholds, so hits can be ranked against each other — it is a
sort key, not a predicted return, and there is still no cross-signal
composite per ticker. Evaluation runs
after each completed crawl and on demand, is deterministic given warehouse
state, and dedupes per (ticker, kind) for 14 days while undismissed. Signals
never toast by default — opt in per kind via `SignalToastKinds` in
appsettings.json. Thresholds live in `%APPDATA%\PortfolioTracker\signals.json`
(created with defaults on first run, re-read on every evaluation — edit and
re-evaluate, no restart). **Signals are screening heuristics over limited free
data, not predictions or advice**; the /signals page says the same thing.

**Data freshness** is visible everywhere: the Sectors page shows per-sector
coverage counts and as-of timestamps (stale data gets a highlight and a
refresh affordance, never a spinner), and the crawler widget shows fresh/stale
ticker counts, the last completed crawl, and budget use.

## Research queue, peer discovery, scoreboard, and AI briefs

> **This tool prioritizes research; it does not recommend investments.** Every
> feature below surfaces and orders things worth looking at — the decision is
> always yours, and nothing in the app produces buy/sell/hold language.

**Why there is no composite score.** The **Queue** page ranks crawled tickers
by *evidence confluence*: six independent dimensions, each judged pass / fail
/ neutral / n-a on its own transparent rule, and the rank is simply the
weighted count of passes divided by the weight of the dimensions that had
data. A single blended number would hide which evidence drove it and invite
false precision; a pass-count with the full breakdown one click away on every
row stays auditable. The dimensions: active undismissed signals (each kind
counts once), sector health quadrant (breadth ≥ 50% × median 20d return ≥ 0
maps to HealthyRally / Recovering / Topping / Breakdown; the first two pass,
Topping is neutral, Breakdown fails), relative value (P/E-P/S-P/B cheapness
composite in the sector's cheapest tercile), quality (growth/margin/ROE
z-mean in the top tercile — also the tie-breaker), catalyst proximity
(earnings within 14 days and 3+ of the last 4 quarters beat), and portfolio
fit (correlation to your portfolio below 0.4). Dimensions without data show
as **n/a and drop out of that ticker's denominator** — missing fundamentals
(ETFs) must never read as a failing grade. Dismissing a row snoozes it for 30
days. The queue reads only the local warehouse; it never calls the network.

**Tuning `queue.json`.** Weights and thresholds live in
`%APPDATA%\PortfolioTracker\queue.json` (created with defaults on first use,
re-read on every queue load — edit and refresh, no restart). Defaults: every
dimension weighs 1.0 except portfolio fit at 1.5; correlation threshold 0.4;
sector-overlap flag at 25% of portfolio value; tercile cutoff 66.7; snooze 30
days. Weights shape the ordering but never hide the breakdown.

**Portfolio fit** correlates each candidate's daily returns against a
composite of your current holdings (value-weighted, from warehoused closes)
over the trailing 90 sessions, requiring 60 overlapping observations — fewer
is reported as "not enough data", never as a low-confidence number. The
composite deliberately uses *current* weights across the whole window: it
answers "how would my portfolio as it stands today have moved with this
candidate", not a historical reconstruction. The queue header shows your
sector weights so the fit column has visible context, and each row carries a
plain-language line ("Low correlation (0.21) to your portfolio; you have no
Materials exposure"). Recomputed nightly after the crawl, or on demand.

**Peer discovery** expands one hop from your held, watchlisted, and
signal-flagged tickers via Finnhub `stock/peers` (7-day cache per seed,
drawing from the same call budget as the rest of the crawler). Peers not in
any universe land in the **Discoveries** panel on the Research page, grouped
by which ticker suggested them: accept adds to a universe (default a
dedicated "Discovered" universe) and queues a full crawl; ignore is permanent
— an ignored ticker never resurfaces. Auto-accept is off by default
(Settings).

**Signal scoreboard** (Signals page → Scoreboard) tracks what happened after
each fire: the ticker's close and every sector member's close are snapshotted
at fire time (membership frozen as of that date, so later universe edits
can't rewrite the benchmark), and 30/60/90-day forward returns fill in as
they mature — both the raw ticker return and the excess over the sector's
median return. Per kind you see fires, matured observations, median excess,
% beating the sector, best/worst examples, and the dismiss rate (a proxy for
perceived noise). **Interpretation caveats, on purpose:** below 10 matured
observations the page says "insufficient data" instead of a percentage;
horizons overlap so consecutive fires aren't independent samples; and the
population is shaped by your own universe choices (survivorship of what you
chose to crawl), so a beat-rate is evidence about the rule *in your universe*,
not the market. The scoreboard is informational by default. An opt-in setting
down-weights queue evidence from kinds whose 60-day beat-rate stays under 45%
across ≥ 20 matured fires — when active, affected queue rows show the applied
weight and the reason.

**AI research briefs** (per queue row, or an optional weekly batch of the top
5) are produced by the **BriefAgent pipeline** — sectioned generation built
for small local models, though the same pipeline runs against the Anthropic
API when selected. The provider is chosen by `AI_VISION_PROVIDER` in the
git-ignored appsettings (`openwebui`, `anthropic`, or `disabled` — the
default until configured); the local provider reads `OPENWEBUI_BASE_URL`,
`OPENWEBUI_API_KEY`, and `OPENWEBUI_MODEL` (key **names** are contract; the
values are credentials to an internal server and belong only in the local
config — never in the repo, samples, or this README). All of it is editable
in Settings, with the key masked and a "Test connection" ping that surfaces
HTTP/SSL/auth errors readably. Briefs need network reach to the local AI
server (VPN); an unreachable server fails the brief button with a clear
message and never blocks the queue or research pages. Requests run strictly
sequentially (shared company hardware), with a 60-second per-call timeout,
`<think>` reasoning blocks stripped before any text is used, and no token
budget on the local provider (usage is logged when the API reports it; the
monthly ceiling and meter still apply to Anthropic).

**Pipeline architecture.** Instead of one big call, each brief is five
sequential section calls — What the company does, Why it surfaced, The 3
numbers that matter most, Bear case / key risks, Questions to answer before
investing — and each section receives only its *slice* of the whitelisted
input snapshot (overview: profile; why-surfaced: the evidence breakdown;
key-numbers: fundamentals + sector medians; bear-case: fundamentals + failed
signal conditions + sector quadrant; questions: everything). Smaller context
per call means better instruction adherence on a 27B-class model. Every
section output then passes a **programmatic grounding check**: all numeric
tokens are extracted (handling %, $, B/M/T suffixes, commas, negatives;
dates excluded) and each must match a number in that section's slice within
2-significant-figure rounding and unit reformatting (0.62 vs 62%, $120B vs a
market cap in millions), plus a word-boundary-aware lint for verdict
vocabulary (buy/sell/hold, target price, under/overvalued, "I recommend" —
while "holdings", "resell", "household", and analyst-rating terms like "buy
ratio" stay legal). A failing section gets exactly ONE repair call listing
its specific violations; if it fails again the section is replaced with a
rendered notice and the brief is flagged **NeedsReview** — unverified numbers
are never stored silently. The checker's guarantee is deliberately narrow: it
verifies the numbers exist in the supplied data, not that the reasoning about
them is sound. Each brief stores per-stage transcripts (prompt, raw response,
cleaned text, check results) in a **generation trace** viewable from the
brief, alongside the exact input snapshot. Pipelines abort (and flag
NeedsReview) after 8 model calls or 8 minutes; a re-request with unchanged
data returns the stored brief (idempotent per input-snapshot hash) unless
Regenerate is explicit, and an interrupted brief resumes from its already-
verified sections.

**Editing the prompts.** The system prompt, one template per section, and the
repair prompt live as plain files in `%APPDATA%\PortfolioTracker\prompts\
brief\` — seeded from embedded defaults on first use, re-read on every
generation (edit and regenerate, no restart), and restorable via "Reset to
defaults" in Settings. Each section template carries a short task spec, an
explicit format contract, and one worked example; on a small model the
example carries most of the instruction-following weight, so edit those with
care. Numbers appearing in a template are automatically whitelisted by the
grounding checker so a format contract like "exactly 3 bullets" can't cause
false failures. The grounding contract itself is unchanged from before: the
model sees ONLY the whitelisted snapshot (profile, queue evidence breakdown,
fundamentals vs sector medians, signal why-panels, portfolio-fit line, last
10 headlines — title/date/source, no URLs), `BriefPayloadTests` fails the
build if any non-whitelisted field ever reaches a payload, every brief is
badged "AI-generated from your local data — not advice", and "Add to
watchlist" prefills the thesis from "Why it surfaced", tagged AI-drafted
until you edit it.

## Taxes

The **Taxes** toolbar button opens a per-year dashboard that estimates the
federal capital gains picture: net short-term and long-term results
(wash-adjusted), transparent netting with the up-to-$3,000 ordinary income
offset and loss carryforwards (auto-chained per year, editable if your real
return differs), gross dividend income per ticker, an estimated federal tax
attributable to investments, and a Form 8949-style CSV export (short/long
sections, an Account column, wash sales flagged with code W) for reconciling
against Chase's 1099-B. **Retirement accounts are invisible to the tax
math**: Roth/Traditional IRA sales and dividends are imported and tracked for
portfolio purposes but are never taxable events — the dashboard notes
"Excludes N retirement account(s)" and defaults to all taxable accounts
combined, with a taxable-only account filter (carryforward applies only on
the all-accounts view, since it is taxpayer-level). A sell simulator ("What
if I sell…", also on the position right-click menu) previews the exact FIFO
lot consumption of a hypothetical sale within the chosen account, its
short/long split, the estimated tax stacked on YTD results, wash interactions
across all accounts, and — most usefully — how many days until each
short-term lot turns long-term and roughly what waiting saves.

**What it estimates and what it ignores.** This is an estimation tool; the
broker's consolidated 1099 and a tax professional are authoritative. Federal
only (built for a no-income-tax state). The model: taxable income = other
income (Settings) minus the standard deduction; net short-term gains stack on
top as ordinary income; net long-term gains stack above that into the LTCG
brackets — all marginal, tiered math in `decimal`. It ignores NIIT, AMT, and
credits. Dividends are tracked as **gross** totals only (see the 1099-DIV for
the qualified/ordinary split — not determinable from the CSV).

**Wash sales.** Detected at the ticker level across **every imported
account, including IRAs**: a taxable loss is disallowed to the extent shares
were purchased in the 61-day window (30 days before through 30 days after the
sale) in ANY account, excluding the shares sold in that sale (same-day split
rows count as one sale); replacement lots match chronologically. The
consequences depend on where the replacement lives:

- **Same account** — classic wash: the disallowed loss is added to the
  replacement lot's basis and the holding period tacks on.
- **Another taxable account** — the loss is deferred the same way, but the
  dashboard flags it "cross-account — will NOT appear on either 1099-B",
  because each broker statement sees only its own side.
- **An IRA (Rev. Rul. 2008-5)** — the loss is **permanently disallowed**: no
  basis adjustment anywhere, the deduction is destroyed. These are flagged in
  red as "IRA wash — loss permanently lost" and totaled on the tax dashboard.
  The sell simulator warns proactively before such a sale, naming recurring
  IRA purchase patterns when it detects one ("Roth ...5678 bought VOO on 6/12
  and 6/26 — selling VOO at a loss before ~7/26 permanently destroys the
  loss").

Wash detection covers all imported accounts including IRAs; it **cannot see
accounts never imported** (a spouse's brokerage, an outside IRA). IRA
contribution limits and eligibility rules are out of scope — the app tracks
activity, not eligibility. Basis and tacking are applied at lot level
(pro-rata within a lot), a slight simplification versus per-share broker
matching. Buy flows (Add Position, manual lots, watchlist "I Bought It",
research view) show a yellow warning when buying would wash a recent loss.

**Updating brackets each year.** Bracket data lives in
`%APPDATA%\PortfolioTracker\taxbrackets.json`, seeded with 2026 values (IRS
Rev. Proc. 2025-32). Add a new year by copying the `"2026"` block, renaming it,
and updating the thresholds from the IRS revenue procedure for that year — no
code change needed. If a year is missing, the nearest earlier year's brackets
are used and the dashboard says so.

All derived tax data (per-lot consumption detail, wash adjustments,
carryforwards) is rebuildable from raw trades at any time via **Rebuild Tax
Data** on the Taxes form.

## Known limitations

- No holiday calendar: auto-refresh runs 9:30 AM–4:00 PM ET Mon–Fri, including market holidays.
- Alerts require the app to be running (tray is enough).
- Realized G/L on oversold positions is approximate until an opening balance is entered.
- Options, shorts, margin, and dividends-as-income tracking are out of scope.
- One quote provider (Finnhub); prices can be ~15 min delayed on the free tier.

## Architecture notes

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
