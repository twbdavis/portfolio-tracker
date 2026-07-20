import { useEffect, useMemo, useState } from 'react'
import { useQueries, useQuery } from '@tanstack/react-query'
import { Bar, BarChart, ResponsiveContainer, Tooltip as ChartTooltip, XAxis, YAxis } from 'recharts'
import { X } from 'lucide-react'
import { toast } from 'sonner'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'
import { gainClass, money, shortDate, signedMoney, signedPercent } from '@/lib/format'
import type {
  EarningsValue,
  FundamentalMetrics,
  FundamentalsValue,
  NewsValue,
  PanelResponse,
  RecommendationTrend,
  RecommendationsValue,
  SnapshotValue,
  SymbolSearchResponse,
  WatchlistItem,
} from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Command,
  CommandEmpty,
  CommandInput,
  CommandItem,
  CommandList,
} from '@/components/ui/command'
import { Skeleton } from '@/components/ui/skeleton'
import { WatchlistEntryDialog } from '@/features/research/WatchlistDialogs'
import {
  SectorContextStrip,
  SignalBadges,
  sectorContextQuery,
} from '@/features/research/SectorContextStrip'

const MAX_COMPARE = 3

// One query per panel: each renders the moment its data clears the server's
// Finnhub rate gate instead of waiting for the slowest panel. Aborted queries
// (ticker switched) release their queued gate slot server-side. Everything but
// the quote is server-cached for hours, so longer client staleTimes are safe.
function panelQuery<T>(ticker: string, panel: string, staleTime: number) {
  return {
    queryKey: ['research', ticker, panel],
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      api.get<PanelResponse<T>>(`/api/research/${encodeURIComponent(ticker)}/${panel}`, signal),
    staleTime,
    enabled: ticker.length > 0,
  }
}

const snapshotQuery = (t: string) => panelQuery<SnapshotValue>(t, 'snapshot', 60_000)
const fundamentalsQuery = (t: string) => panelQuery<FundamentalsValue>(t, 'fundamentals', 10 * 60_000)
const recommendationsQuery = (t: string) => panelQuery<RecommendationsValue>(t, 'recommendations', 10 * 60_000)
const earningsQuery = (t: string) => panelQuery<EarningsValue>(t, 'earnings', 10 * 60_000)
const newsQuery = (t: string) => panelQuery<NewsValue>(t, 'news', 10 * 60_000)

/// $1.23T / $45.6B / $789M — HumanNumber.Money for market caps.
function humanMoney(v: number): string {
  const abs = Math.abs(v)
  if (abs >= 1e12) return `$${(v / 1e12).toFixed(2)}T`
  if (abs >= 1e9) return `$${(v / 1e9).toFixed(2)}B`
  if (abs >= 1e6) return `$${(v / 1e6).toFixed(1)}M`
  return money(v)
}

const num = (v: number | null | undefined) => (v == null ? '—' : v.toFixed(2))
const pct1 = (v: number | null | undefined) => (v == null ? '—' : `${v.toFixed(1)}%`)

export function ResearchTab({
  ticker,
  onOpenTicker,
}: {
  ticker: string | null
  onOpenTicker: (ticker: string) => void
}) {
  const t = ticker ?? ''
  const snapshot = useQuery(snapshotQuery(t))
  const fundamentals = useQuery(fundamentalsQuery(t))
  const recommendations = useQuery(recommendationsQuery(t))
  const earnings = useQuery(earningsQuery(t))
  const news = useQuery(newsQuery(t))

  const watchlist = useQuery({
    queryKey: ['watchlist'],
    queryFn: () => api.get<WatchlistItem[]>('/api/watchlist'),
  })
  const watchItem = watchlist.data?.find((w) => w.ticker === ticker) ?? null

  const [watchOpen, setWatchOpen] = useState(false)
  const [compare, setCompare] = useState<string[]>([])

  const addToCompare = () => {
    if (ticker == null || compare.includes(ticker)) return
    if (compare.length >= MAX_COMPARE) {
      toast.warning(`Compare holds up to ${MAX_COMPARE} tickers. Remove one first.`)
      return
    }
    setCompare([...compare, ticker])
  }

  return (
    <div className="space-y-4 p-4">
      <SearchBox onPick={onOpenTicker} />

      {ticker == null && (
        <div className="flex flex-col items-center gap-2 py-24 text-center">
          <p className="text-lg font-medium">Look up a ticker</p>
          <p className="max-w-md text-sm text-muted-foreground">
            Search by symbol or company name — snapshot, fundamentals, analyst calls, earnings, and
            news load per panel.
          </p>
        </div>
      )}

      {ticker != null && (
        <>
          <SnapshotCard
            ticker={ticker}
            resp={snapshot.data}
            loading={snapshot.isLoading}
            metrics={fundamentals.data?.value?.metrics ?? null}
          />

          <div className="flex flex-wrap items-center gap-2">
            <Button size="sm" variant="outline" onClick={() => setWatchOpen(true)}>
              {watchItem ? 'Edit Watchlist Entry' : 'Add to Watchlist'}
            </Button>
            <Button
              size="sm"
              variant="outline"
              onClick={addToCompare}
              disabled={compare.includes(ticker)}
            >
              Add to Compare ({compare.length}/{MAX_COMPARE})
            </Button>
            <SignalBadges ticker={ticker} />
            {watchItem?.thesis && (
              <span className="text-xs text-muted-foreground">
                Thesis ({watchItem.conviction}/5): {watchItem.thesis}
              </span>
            )}
          </div>

          <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
            <div className="space-y-4">
              <FundamentalsPanel resp={fundamentals.data} loading={fundamentals.isLoading} />
              <SectorContextStrip ticker={ticker} />
            </div>
            <RecommendationsPanel resp={recommendations.data} loading={recommendations.isLoading} />
            <EarningsPanel resp={earnings.data} loading={earnings.isLoading} />
            <NewsPanel resp={news.data} loading={news.isLoading} />
          </div>
        </>
      )}

      {compare.length > 0 && (
        <CompareSection tickers={compare} onRemove={(c) => setCompare(compare.filter((x) => x !== c))} />
      )}

      <WatchlistEntryDialog
        open={watchOpen}
        onOpenChange={setWatchOpen}
        existing={watchItem}
        fixedTicker={ticker ?? undefined}
        name={snapshot.data?.value?.profile?.name ?? null}
      />
    </div>
  )
}

// ---------- search ----------

function SearchBox({ onPick }: { onPick: (ticker: string) => void }) {
  const [text, setText] = useState('')
  const [debounced, setDebounced] = useState('')

  useEffect(() => {
    const t = setTimeout(() => setDebounced(text.trim()), 300)
    return () => clearTimeout(t)
  }, [text])

  const search = useQuery({
    queryKey: ['research-search', debounced],
    queryFn: ({ signal }) =>
      api.get<SymbolSearchResponse>(
        `/api/research/search?q=${encodeURIComponent(debounced)}`,
        signal,
      ),
    enabled: debounced.length > 0,
    staleTime: 5 * 60_000, // backspacing to a previous query re-hits the cache instantly
    placeholderData: (prev) => prev, // keep the old list visible while the new one loads
  })

  const pick = (symbol: string) => {
    setText('')
    setDebounced('')
    onPick(symbol)
  }

  return (
    <Command shouldFilter={false} className="max-w-xl rounded-md border">
      <CommandInput
        placeholder="Ticker or company name"
        value={text}
        onValueChange={setText}
      />
      {debounced.length > 0 && (
        <CommandList>
          {search.data?.error && (
            <div className="px-3 py-2 text-sm text-warn">{search.data.error}</div>
          )}
          {search.isFetching && !search.data && (
            <div className="px-3 py-2 text-sm text-muted-foreground">Searching…</div>
          )}
          {search.data && !search.data.error && (
            <CommandEmpty>No matches.</CommandEmpty>
          )}
          {search.data?.results.slice(0, 20).map((r) => (
            <CommandItem key={r.symbol} value={r.symbol} onSelect={() => pick(r.symbol)}>
              <span className="font-semibold">{r.symbol}</span>
              <span className="truncate text-muted-foreground">{r.description}</span>
              <span className="ml-auto text-xs text-muted-foreground">{r.type}</span>
            </CommandItem>
          ))}
        </CommandList>
      )}
    </Command>
  )
}

// ---------- panels ----------

function Panel({
  title,
  status,
  loading = false,
  children,
}: {
  title: string
  status?: string | null
  loading?: boolean
  children: React.ReactNode
}) {
  return (
    <Card className="gap-3 py-4">
      <CardHeader className="px-4">
        <CardTitle className="flex items-baseline justify-between text-xs font-medium tracking-wide text-muted-foreground uppercase">
          {title}
          {status && <span className="text-[11px] font-normal normal-case">{status}</span>}
        </CardTitle>
      </CardHeader>
      <CardContent className="px-4">
        {loading ? <Skeleton className="h-40 w-full" /> : children}
      </CardContent>
    </Card>
  )
}

/// Current price positioned on the 52-week min–max track.
function RangeBar({
  low,
  high,
  current,
}: {
  low: number | null
  high: number | null
  current: number | null
}) {
  const frac =
    low != null && high != null && high > low && current != null
      ? Math.min(1, Math.max(0, (current - low) / (high - low)))
      : null
  return (
    <div className="relative my-2 h-1 w-full max-w-80 rounded bg-border">
      {frac != null && (
        <div
          className="absolute -top-1 size-3 rounded-full bg-foreground"
          style={{ left: `calc(${(frac * 100).toFixed(1)}% - 6px)` }}
        />
      )}
    </div>
  )
}

function SnapshotCard({
  ticker,
  resp,
  loading,
  metrics,
}: {
  ticker: string
  resp: PanelResponse<SnapshotValue> | undefined
  loading: boolean
  metrics: FundamentalMetrics | null // 52-week range arrives with fundamentals
}) {
  const quote = resp?.value?.quote ?? null
  const profile = resp?.value?.profile ?? null
  const meta = profile
    ? [
        profile.industry,
        profile.marketCapMillions != null
          ? `Market cap ${humanMoney(profile.marketCapMillions * 1_000_000)}`
          : null,
        profile.ipoDate
          ? `IPO ${new Date(profile.ipoDate + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', year: 'numeric' })}`
          : null,
      ]
        .filter(Boolean)
        .join('   ·   ')
    : ''
  const belowHigh =
    metrics?.high52 != null && metrics.high52 > 0 && quote != null
      ? ((metrics.high52 - quote.current) / metrics.high52) * 100
      : null

  return (
    <Panel
      title="Snapshot"
      loading={loading}
      status={resp?.error ?? (resp && profile == null ? 'profile not available' : '')}
    >
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="min-w-64">
          <h2 className="text-lg font-semibold">
            {profile?.name ? `${profile.name}  (${ticker})` : ticker}
          </h2>
          <p className="text-sm whitespace-pre text-muted-foreground">{meta}</p>
          {profile?.webUrl?.startsWith('http') && (
            <a
              href={profile.webUrl}
              target="_blank"
              rel="noreferrer"
              className="text-sm text-primary hover:underline"
            >
              {profile.webUrl}
            </a>
          )}
        </div>
        <div className="min-w-80">
          {quote ? (
            <>
              <div className="flex items-baseline gap-2">
                <span className="text-2xl font-semibold tnum">{money(quote.current)}</span>
                <span className={cn('text-sm font-medium tnum', gainClass(quote.dayChange))}>
                  {signedMoney(quote.dayChange)}
                  {quote.dayChangePct != null && ` (${signedPercent(quote.dayChangePct)})`}
                </span>
              </div>
              <RangeBar
                low={metrics?.low52 ?? null}
                high={metrics?.high52 ?? null}
                current={quote.current}
              />
              {belowHigh != null && (
                <p className="text-xs text-muted-foreground tnum">
                  {belowHigh.toFixed(1)}% below 52-week high ({money(metrics?.low52)} –{' '}
                  {money(metrics?.high52)})
                </p>
              )}
            </>
          ) : (
            <span className="text-sm text-muted-foreground">no quote</span>
          )}
        </div>
      </div>
    </Panel>
  )
}

const METRIC_GROUPS: {
  group: string
  rows: { key: keyof FundamentalMetrics; label: string; percent: boolean }[]
}[] = [
  {
    group: 'Valuation',
    rows: [
      { key: 'peTtm', label: 'P/E (TTM)', percent: false },
      { key: 'ps', label: 'P/S', percent: false },
      { key: 'pb', label: 'P/B', percent: false },
    ],
  },
  {
    group: 'Growth',
    rows: [
      { key: 'revenueGrowthYoY', label: 'Revenue growth YoY', percent: true },
      { key: 'revenueGrowth3Y', label: 'Revenue growth 3Y', percent: true },
      { key: 'epsGrowthYoY', label: 'EPS growth YoY', percent: true },
    ],
  },
  {
    group: 'Profitability',
    rows: [
      { key: 'grossMargin', label: 'Gross margin', percent: true },
      { key: 'netMargin', label: 'Net margin', percent: true },
      { key: 'roe', label: 'ROE', percent: true },
    ],
  },
  {
    group: 'Risk',
    rows: [{ key: 'beta', label: 'Beta', percent: false }],
  },
]

function FundamentalsPanel({
  resp,
  loading,
}: {
  resp: PanelResponse<FundamentalsValue> | undefined
  loading: boolean
}) {
  const metrics = resp?.value?.metrics ?? null
  return (
    <Panel
      title="Fundamentals"
      loading={loading}
      status={resp?.error ?? (resp && metrics == null ? 'not available (normal for ETFs)' : '')}
    >
      <div className="grid grid-cols-2 gap-3">
        {METRIC_GROUPS.map(({ group, rows }) => (
          <div key={group} className="rounded-md border p-3">
            <h4 className="mb-1.5 text-[11px] font-medium tracking-wide text-muted-foreground uppercase">
              {group}
            </h4>
            {rows.map(({ key, label, percent }) => {
              const v = metrics?.[key] ?? null
              return (
                <div key={key} className="flex items-baseline justify-between py-0.5 text-sm">
                  <span className="text-muted-foreground">{label}</span>
                  <span className="font-medium tnum">{percent ? pct1(v) : num(v)}</span>
                </div>
              )
            })}
          </div>
        ))}
      </div>
    </Panel>
  )
}

// strong buy → strong sell, green → red (the WinForms TrendBars segments)
const TREND_SEGMENTS: { key: keyof RecommendationTrend & string; label: string; color: string }[] = [
  { key: 'strongBuy', label: 'Strong buy', color: '#1e7f4f' },
  { key: 'buy', label: 'Buy', color: '#7cb342' },
  { key: 'hold', label: 'Hold', color: '#f0c63c' },
  { key: 'sell', label: 'Sell', color: '#e07b39' },
  { key: 'strongSell', label: 'Strong sell', color: '#c2402a' },
]

function RecommendationsPanel({
  resp,
  loading,
}: {
  resp: PanelResponse<RecommendationsValue> | undefined
  loading: boolean
}) {
  const trends = useMemo(() => resp?.value?.trends ?? [], [resp])
  const chartData = useMemo(
    () =>
      [...trends].reverse().map((t) => ({
        ...t,
        month: new Date(t.period + (t.period.length === 10 ? 'T00:00:00' : '')).toLocaleDateString(
          'en-US',
          { month: 'short', year: '2-digit' },
        ),
      })),
    [trends],
  )
  return (
    <Panel
      title="Analyst recommendations"
      loading={loading}
      status={resp?.error ?? (resp && trends.length === 0 ? 'no analyst coverage' : '')}
    >
      {chartData.length > 0 && (
        <ResponsiveContainer width="100%" height={220}>
          <BarChart data={chartData}>
            <XAxis dataKey="month" tick={{ fontSize: 11 }} tickLine={false} axisLine={false} />
            <YAxis allowDecimals={false} tick={{ fontSize: 11 }} tickLine={false} axisLine={false} width={32} />
            <ChartTooltip
              cursor={{ fill: 'transparent' }}
              contentStyle={{
                background: 'var(--popover)',
                border: '1px solid var(--border)',
                borderRadius: 6,
                fontSize: 12,
                color: 'var(--popover-foreground)',
              }}
            />
            {TREND_SEGMENTS.map((s) => (
              <Bar key={s.key} dataKey={s.key} name={s.label} stackId="rec" fill={s.color} />
            ))}
          </BarChart>
        </ResponsiveContainer>
      )}
      {chartData.length > 0 && (
        <div className="mt-2 flex flex-wrap gap-3">
          {TREND_SEGMENTS.map((s) => (
            <span key={s.key} className="flex items-center gap-1 text-xs text-muted-foreground">
              <span className="size-2 rounded-sm" style={{ background: s.color }} />
              {s.label}
            </span>
          ))}
        </div>
      )}
    </Panel>
  )
}

function EarningsPanel({
  resp,
  loading,
}: {
  resp: PanelResponse<EarningsValue> | undefined
  loading: boolean
}) {
  const quarters = resp?.value?.quarters ?? []
  const nextEarningsDate = resp?.value?.nextEarningsDate ?? null
  let nextText = 'Next earnings: unknown'
  let soon = false
  if (nextEarningsDate) {
    const next = new Date(nextEarningsDate + 'T00:00:00')
    const days = Math.round((next.getTime() - new Date().setHours(0, 0, 0, 0)) / 86_400_000)
    soon = days <= 7
    nextText =
      `Next earnings: ${next.toLocaleDateString('en-US', { month: 'short', day: 'numeric' })}` +
      (soon ? `  ⚠ in ${days} day${days === 1 ? '' : 's'}` : '')
  }
  return (
    <Panel
      title="Earnings"
      loading={loading}
      status={resp?.error ?? (resp && quarters.length === 0 ? 'no earnings history' : '')}
    >
      <p className={cn('mb-2 text-sm font-medium', soon && 'text-warn')}>{nextText}</p>
      {quarters.length > 0 && (
        <table className="w-full text-xs">
          <thead>
            <tr className="border-b text-muted-foreground">
              <th className="px-2 py-1.5 text-left">Period</th>
              <th className="px-2 py-1.5 text-right">EPS est.</th>
              <th className="px-2 py-1.5 text-right">EPS actual</th>
              <th className="px-2 py-1.5 text-right">Surprise</th>
            </tr>
          </thead>
          <tbody>
            {quarters.map((q) => (
              <tr key={q.period} className="border-b border-border/50 last:border-0">
                <td className="px-2 py-1 tnum">{q.period}</td>
                <td className="px-2 py-1 text-right tnum">{num(q.estimate)}</td>
                <td className="px-2 py-1 text-right tnum">{num(q.actual)}</td>
                <td className={cn('px-2 py-1 text-right tnum', gainClass(q.surprisePercent))}>
                  {signedPercent(q.surprisePercent)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Panel>
  )
}

function NewsPanel({
  resp,
  loading,
}: {
  resp: PanelResponse<NewsValue> | undefined
  loading: boolean
}) {
  const items = resp?.value?.items ?? []
  return (
    <Panel
      title="News (last 30 days)"
      loading={loading}
      status={resp?.error ?? (resp && items.length === 0 ? 'no recent news' : '')}
    >
      <div className="max-h-80 space-y-3 overflow-y-auto pr-1">
        {items.slice(0, 40).map((n) => (
          <div key={n.url}>
            <p className="text-xs text-muted-foreground">
              {shortDate(n.publishedAt)} · {n.source}
            </p>
            <a
              href={n.url}
              target="_blank"
              rel="noreferrer"
              className="text-sm leading-snug hover:underline"
            >
              {n.headline}
            </a>
          </div>
        ))}
      </div>
    </Panel>
  )
}

// ---------- compare ----------

/// What compare needs per ticker: snapshot + fundamentals + newest analyst
/// trend. Fetched via the same panel queries as the main view, so a compared
/// ticker that was just researched costs zero extra API calls.
interface CompareData {
  snapshot: SnapshotValue | null
  fundamentals: FundamentalsValue | null
  recommendations: RecommendationsValue | null
}

type CompareRowDef = {
  label: string
  best: 'none' | 'lowest' | 'highest'
  value: (d: CompareData) => number | null
  fmt: (v: number) => string
  /// Key into the warehouse sector context for the optional median column.
  sectorKey?: string
}

const COMPARE_ROWS: CompareRowDef[] = [
  { label: 'Price', best: 'none', value: (d) => d.snapshot?.quote?.current ?? null, fmt: (v) => money(v) },
  {
    label: 'Market cap',
    best: 'none',
    value: (d) => d.snapshot?.profile?.marketCapMillions ?? null,
    fmt: (v) => humanMoney(v * 1_000_000),
  },
  { label: 'P/E (TTM)', best: 'lowest', value: (d) => d.fundamentals?.metrics?.peTtm ?? null, fmt: num, sectorKey: 'pe' },
  { label: 'P/S', best: 'lowest', value: (d) => d.fundamentals?.metrics?.ps ?? null, fmt: num, sectorKey: 'ps' },
  {
    label: 'Revenue growth YoY',
    best: 'highest',
    value: (d) => d.fundamentals?.metrics?.revenueGrowthYoY ?? null,
    fmt: pct1,
    sectorKey: 'revenueGrowthYoY',
  },
  { label: 'Gross margin', best: 'highest', value: (d) => d.fundamentals?.metrics?.grossMargin ?? null, fmt: pct1, sectorKey: 'grossMargin' },
  { label: 'Net margin', best: 'highest', value: (d) => d.fundamentals?.metrics?.netMargin ?? null, fmt: pct1, sectorKey: 'netMargin' },
  { label: 'ROE', best: 'highest', value: (d) => d.fundamentals?.metrics?.roe ?? null, fmt: pct1, sectorKey: 'roe' },
  { label: 'Beta', best: 'none', value: (d) => d.fundamentals?.metrics?.beta ?? null, fmt: num },
  {
    label: '% below 52-week high',
    best: 'highest',
    value: (d) => {
      const high = d.fundamentals?.metrics?.high52
      const cur = d.snapshot?.quote?.current
      return high != null && high > 0 && cur != null ? ((high - cur) / high) * 100 : null
    },
    fmt: pct1,
    sectorKey: 'pctOff52wHigh',
  },
  {
    label: 'Analyst buy ratio',
    best: 'highest',
    value: (d) => {
      const t = d.recommendations?.trends[0] // newest first
      if (!t) return null
      const total = t.strongBuy + t.buy + t.hold + t.sell + t.strongSell
      return total === 0 ? null : ((t.strongBuy + t.buy) / total) * 100
    },
    fmt: (v) => `${v.toFixed(0)}%`,
  },
]

/// Side-by-side fundamentals; highlight = best value in the row (lowest P/E
/// and P/S, highest growth, margins, ROE). No composite score on purpose.
function CompareSection({
  tickers,
  onRemove,
}: {
  tickers: string[]
  onRemove: (ticker: string) => void
}) {
  const results = useQueries({
    queries: tickers.flatMap((t) => [snapshotQuery(t), fundamentalsQuery(t), recommendationsQuery(t)]),
  })
  // optional pseudo-column: the first compared ticker's sector medians (all
  // compared tickers usually share a sector; the header names it to be safe)
  const [showMedian, setShowMedian] = useState(false)
  const sectorCtx = useQuery({ ...sectorContextQuery(tickers[0] ?? ''), enabled: showMedian && tickers.length > 0 })
  const medianByKey = new Map(
    (sectorCtx.data?.metrics ?? []).map((m) => [m.key, m.sectorMedian] as const),
  )
  const data = new Map<string, CompareData>(
    tickers.map((t, i) => [
      t,
      {
        snapshot: (results[i * 3].data as PanelResponse<SnapshotValue> | undefined)?.value ?? null,
        fundamentals:
          (results[i * 3 + 1].data as PanelResponse<FundamentalsValue> | undefined)?.value ?? null,
        recommendations:
          (results[i * 3 + 2].data as PanelResponse<RecommendationsValue> | undefined)?.value ??
          null,
      },
    ]),
  )
  const loading = results.some((r) => r.isLoading)

  return (
    <Panel title={`Compare (${tickers.length}/${MAX_COMPARE})`} status={loading ? 'loading…' : ''}>
      <div className="mb-3 flex flex-wrap items-center gap-2">
        {tickers.map((t) => (
          <Badge key={t} variant="secondary" className="gap-1 pr-1">
            {t}
            <button
              className="rounded-sm p-0.5 hover:bg-accent"
              onClick={() => onRemove(t)}
              aria-label={`Remove ${t} from compare`}
            >
              <X className="size-3" />
            </button>
          </Badge>
        ))}
        <Button
          size="sm"
          variant={showMedian ? 'secondary' : 'outline'}
          className="h-6 px-2 text-xs"
          onClick={() => setShowMedian(!showMedian)}
        >
          Sector median
        </Button>
      </div>
      <table className="w-full max-w-2xl text-sm">
        <thead>
          <tr className="border-b text-xs text-muted-foreground">
            <th className="px-2 py-1.5 text-left">Metric</th>
            {tickers.map((t) => (
              <th key={t} className="px-2 py-1.5 text-right">
                {t}
              </th>
            ))}
            {showMedian && (
              <th className="px-2 py-1.5 text-right text-muted-foreground">
                {sectorCtx.data?.sector ? `${sectorCtx.data.sector} median` : 'Sector median'}
              </th>
            )}
          </tr>
        </thead>
        <tbody>
          {COMPARE_ROWS.map((row) => {
            const values = tickers.map((t) => {
              const d = data.get(t)
              return d ? row.value(d) : null
            })
            const present = values.filter((v): v is number => v != null)
            const bestVal =
              row.best !== 'none' && present.length >= 2
                ? row.best === 'lowest'
                  ? Math.min(...present)
                  : Math.max(...present)
                : null
            return (
              <tr key={row.label} className="border-b border-border/50 last:border-0">
                <td className="px-2 py-1 text-muted-foreground">{row.label}</td>
                {values.map((v, i) => (
                  <td
                    key={tickers[i]}
                    className={cn(
                      'px-2 py-1 text-right tnum',
                      bestVal != null && v === bestVal && 'bg-gain/15 font-medium',
                    )}
                  >
                    {v == null ? '—' : row.fmt(v)}
                  </td>
                ))}
                {showMedian && (
                  <td className="px-2 py-1 text-right tnum text-muted-foreground">
                    {(() => {
                      const med = row.sectorKey ? medianByKey.get(row.sectorKey) : null
                      return med == null ? '—' : row.fmt(med)
                    })()}
                  </td>
                )}
              </tr>
            )
          })}
        </tbody>
      </table>
    </Panel>
  )
}
