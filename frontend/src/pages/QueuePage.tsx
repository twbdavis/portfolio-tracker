import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { Bot, ChevronDown, ChevronRight, Info, ListChecks, RefreshCw, X } from 'lucide-react'
import { toast } from 'sonner'
import { api, ApiError } from '@/lib/api'
import { cn } from '@/lib/utils'
import { shortDate } from '@/lib/format'
import type { QueueEvidence, QueueResponse, QueueRow } from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Switch } from '@/components/ui/switch'
import { Label } from '@/components/ui/label'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { BriefDialog } from '@/features/queue/BriefDialog'
import { BriefsTab } from '@/features/queue/BriefsTab'
import { SearchBox } from '@/features/research/SearchBox'

const DIMENSION_LABELS: Record<string, string> = {
  Signals: 'Signals',
  SectorHealth: 'Sector health',
  RelativeValue: 'Relative value',
  Quality: 'Quality',
  Catalyst: 'Catalyst',
  PortfolioFit: 'Portfolio fit',
}

const STATUS_STYLES: Record<string, string> = {
  Pass: 'border-gain/60 text-gain',
  Fail: 'border-loss/60 text-loss',
  Neutral: 'border-warn/60 text-warn',
  NotAvailable: 'border-border text-muted-foreground',
}

const STATUS_LABELS: Record<string, string> = {
  Pass: 'pass',
  Fail: 'fail',
  Neutral: 'neutral',
  NotAvailable: 'n/a',
}

/// The research priority queue: crawled tickers ranked by evidence
/// confluence. Deliberately never a single opaque number — the ratio shown is
/// a weighted pass count whose full breakdown is one click away on every row.
export function QueuePage() {
  const queryClient = useQueryClient()
  const [includeHeld, setIncludeHeld] = useState(false)
  const [briefTicker, setBriefTicker] = useState<string | null>(null)

  const queue = useQuery({
    queryKey: ['queue', includeHeld],
    queryFn: () => api.get<QueueResponse>(`/api/queue?includeHeld=${includeHeld}`),
  })

  const runJobs = useMutation({
    mutationFn: () => api.post<{ fitRows: number; outcomeRows: number }>('/api/queue/jobs/run'),
    onSuccess: (r) => {
      toast.success(`Jobs ran: ${r.fitRows} fit rows, ${r.outcomeRows} outcome updates`)
      queryClient.invalidateQueries({ queryKey: ['queue'] })
      queryClient.invalidateQueries({ queryKey: ['scoreboard'] })
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : String(e)),
  })

  const dismiss = useMutation({
    mutationFn: (ticker: string) => api.post(`/api/queue/${ticker}/dismiss`),
    onSuccess: (_, ticker) => {
      toast.success(`${ticker} snoozed from the queue for 30 days`)
      queryClient.invalidateQueries({ queryKey: ['queue'] })
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : String(e)),
  })

  const data = queue.data

  return (
    <Tabs defaultValue="queue">
      <div className="flex items-center gap-3 border-b px-4 pt-3 pb-2">
        <h1 className="flex items-center gap-2 text-lg font-semibold">
          <ListChecks className="size-5" /> Research queue
        </h1>
        <TabsList>
          <TabsTrigger value="queue">Queue</TabsTrigger>
          <TabsTrigger value="briefs">Briefs</TabsTrigger>
        </TabsList>
        {/* brief any ticker, queued or not — picking a result opens the
            BriefDialog, which generates on demand */}
        <div className="ml-auto w-64">
          <SearchBox
            placeholder="AI brief for any ticker…"
            className="rounded-md border"
            floating
            onPick={(t) => setBriefTicker(t)}
          />
        </div>
      </div>

      <TabsContent value="queue">
        <div className="flex flex-col gap-4 p-4">
          <div className="flex flex-wrap items-center justify-end gap-3">
            <div className="flex items-center gap-2">
              <Switch id="include-held" checked={includeHeld} onCheckedChange={setIncludeHeld} />
              <Label htmlFor="include-held" className="text-xs font-normal">
                Include held positions
              </Label>
            </div>
            <Button
              variant="outline"
              size="sm"
              onClick={() => runJobs.mutate()}
              disabled={runJobs.isPending}
            >
              <RefreshCw className={cn('size-4', runJobs.isPending && 'animate-spin')} />
              Recompute fit &amp; outcomes
            </Button>
          </div>

          <div className="flex items-start gap-2 rounded-md border border-warn/40 bg-warn/5 px-3 py-2 text-xs text-muted-foreground">
            <Info className="mt-0.5 size-3.5 shrink-0" />
            <span>
              The queue ranks tickers by how many independent evidence dimensions pass — it
              prioritizes research, it does not recommend investments. Expand any row for the exact
              numbers behind each check; n/a dimensions drop out of that ticker&apos;s denominator.
            </span>
          </div>

          {data && data.portfolioSectorWeights.length > 0 && (
            <div className="rounded-lg border bg-card p-3">
              <div className="mb-2 flex items-center justify-between text-xs text-muted-foreground">
                <span className="font-medium text-foreground">
                  Your portfolio&apos;s sector weights
                </span>
                <span>
                  {data.holdingsCount} holding
                  {data.holdingsCount === 1 ? '' : 's'} · fit column is measured against this mix
                  {data.jobsLastRanAt && ` · fit computed ${shortDate(data.jobsLastRanAt)}`}
                </span>
              </div>
              <div className="flex flex-wrap gap-1.5">
                {data.portfolioSectorWeights.map((w) => (
                  <Badge
                    key={w.sector}
                    variant="secondary"
                    className="px-2 py-0.5 text-[11px] tnum"
                  >
                    {w.sector} {w.weightPct.toFixed(0)}%
                  </Badge>
                ))}
              </div>
            </div>
          )}

          {queue.isLoading && (
            <div className="rounded-lg border bg-card p-8 text-center text-sm text-muted-foreground">
              Loading…
            </div>
          )}

          {data && data.rows.length === 0 && (
            <div className="rounded-lg border bg-card p-8 text-center text-sm text-muted-foreground">
              Nothing to rank yet. Add universes (Sectors page) so the crawler can warehouse
              candidates
              {data.snoozedCount > 0 && `, or wait out ${data.snoozedCount} snoozed ticker(s)`}.
            </div>
          )}

          <div className="flex flex-col gap-2">
            {data?.rows.map((row, i) => (
              <QueueRowCard
                key={row.ticker}
                rank={i + 1}
                row={row}
                downWeightingActive={data.downWeightingActive}
                onDismiss={() => dismiss.mutate(row.ticker)}
                onBrief={() => setBriefTicker(row.ticker)}
              />
            ))}
          </div>

          {data && data.snoozedCount > 0 && data.rows.length > 0 && (
            <p className="text-xs text-muted-foreground">
              {data.snoozedCount} dismissed ticker
              {data.snoozedCount === 1 ? '' : 's'} snoozed for 30 days.
            </p>
          )}
        </div>
      </TabsContent>

      <TabsContent value="briefs">
        <BriefsTab />
      </TabsContent>

      <BriefDialog
        ticker={briefTicker}
        open={briefTicker !== null}
        onOpenChange={(open) => !open && setBriefTicker(null)}
      />
    </Tabs>
  )
}

function QueueRowCard({
  rank,
  row,
  downWeightingActive,
  onDismiss,
  onBrief,
}: {
  rank: number
  row: QueueRow
  downWeightingActive: boolean
  onDismiss: () => void
  onBrief: () => void
}) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)

  const addToWatchlist = useMutation({
    mutationFn: () =>
      api.post('/api/watchlist', {
        ticker: row.ticker,
        name: null,
        thesis: `[Machine-generated starting point from the research queue — verify before acting]\nSurfaced with ${row.weightedPasses} of ${row.applicableWeight} weighted evidence passing. ${row.fitLine}`,
        conviction: 3,
        targetBuyPrice: null,
        alertEnabled: false,
      }),
    onSuccess: () => {
      toast.success(`${row.ticker} added to watchlist`)
      queryClient.invalidateQueries({ queryKey: ['queue'] })
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : String(e)),
  })

  const pct = row.applicableWeight > 0 ? (row.weightedPasses / row.applicableWeight) * 100 : 0

  return (
    <div className="rounded-lg border bg-card p-3">
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <span className="w-6 text-right text-xs text-muted-foreground tnum">{rank}.</span>
            <span className="font-semibold">{row.ticker}</span>
            {row.sector && (
              <Badge variant="secondary" className="px-1.5 py-0 text-[10px]">
                {row.sector}
              </Badge>
            )}
            {row.isHeld && (
              <Badge variant="outline" className="px-1.5 py-0 text-[10px]">
                Held
              </Badge>
            )}
            {row.onWatchlist && (
              <Badge variant="outline" className="px-1.5 py-0 text-[10px]">
                Watchlist
              </Badge>
            )}
            <Tooltip>
              <TooltipTrigger asChild>
                <span className="flex items-center gap-1.5">
                  <span className="h-1.5 w-20 overflow-hidden rounded-full bg-muted">
                    <span
                      className="block h-full rounded-full bg-primary"
                      style={{ width: `${pct}%` }}
                    />
                  </span>
                  <span className="text-xs text-muted-foreground tnum">
                    {row.weightedPasses} / {row.applicableWeight}
                  </span>
                </span>
              </TooltipTrigger>
              <TooltipContent className="max-w-xs">
                Weighted evidence passing: {row.weightedPasses} of {row.applicableWeight} applicable
                weight. Not a composite score — expand the row for the exact checks; n/a dimensions
                are excluded from the denominator.
              </TooltipContent>
            </Tooltip>
          </div>
          <div className="mt-1 flex flex-wrap items-center gap-1.5 pl-8">
            {row.evidence.map((e) => (
              <EvidenceChip key={e.dimension} evidence={e} />
            ))}
          </div>
          <p className="mt-1 pl-8 text-xs text-muted-foreground">{row.fitLine}</p>
        </div>
        <div className="flex shrink-0 items-center gap-1">
          <Button
            variant="ghost"
            size="sm"
            className="h-7 px-2 text-xs"
            onClick={() =>
              navigate(`/research?ticker=${row.ticker}`, {
                state: { back: { path: '/queue', label: 'Queue' } },
              })
            }
          >
            Research
          </Button>
          {!row.onWatchlist && (
            <Button
              variant="ghost"
              size="sm"
              className="h-7 px-2 text-xs"
              onClick={() => addToWatchlist.mutate()}
              disabled={addToWatchlist.isPending}
            >
              Watch
            </Button>
          )}
          <Button variant="ghost" size="sm" className="h-7 px-2 text-xs" onClick={onBrief}>
            <Bot className="size-3.5" />
            {row.latestBriefId ? 'Brief' : 'AI brief'}
          </Button>
          <Tooltip>
            <TooltipTrigger asChild>
              <Button variant="ghost" size="icon" className="size-7" onClick={onDismiss}>
                <X className="size-3.5" />
              </Button>
            </TooltipTrigger>
            <TooltipContent>Dismiss from queue (30-day snooze)</TooltipContent>
          </Tooltip>
        </div>
      </div>

      <button
        className="mt-2 flex items-center gap-1 pl-8 text-xs text-muted-foreground hover:text-foreground"
        onClick={() => setOpen(!open)}
      >
        {open ? <ChevronDown className="size-3" /> : <ChevronRight className="size-3" />}
        Evidence breakdown
      </button>
      {open && (
        <div className="mt-2 pl-8">
          <table className="w-full text-xs">
            <thead>
              <tr className="border-b text-muted-foreground">
                <th className="py-1 pr-2 text-left font-medium">Dimension</th>
                <th className="py-1 pr-2 text-left font-medium">Observed</th>
                <th className="py-1 pr-2 text-left font-medium">Threshold</th>
                <th className="py-1 pr-2 text-left font-medium">Status</th>
                <th className="py-1 pr-2 text-right font-medium">Weight</th>
                <th className="py-1 text-left font-medium">As of</th>
              </tr>
            </thead>
            <tbody>
              {row.evidence.map((e) => (
                <tr key={e.dimension} className="border-b border-border/50 align-top">
                  <td className="py-1 pr-2 whitespace-nowrap">{e.label}</td>
                  <td className="py-1 pr-2">
                    {e.observed}
                    {e.note && <div className="mt-0.5 text-[10px] text-warn">{e.note}</div>}
                  </td>
                  <td className="py-1 pr-2">{e.threshold}</td>
                  <td
                    className={cn('py-1 pr-2 font-medium whitespace-nowrap', {
                      'text-gain': e.status === 'Pass',
                      'text-loss': e.status === 'Fail',
                      'text-warn': e.status === 'Neutral',
                      'text-muted-foreground': e.status === 'NotAvailable',
                    })}
                  >
                    {STATUS_LABELS[e.status]}
                  </td>
                  <td className="py-1 pr-2 text-right tnum whitespace-nowrap">
                    {e.status === 'NotAvailable'
                      ? '—'
                      : downWeightingActive && e.appliedWeight !== e.weight
                        ? `${e.appliedWeight} (of ${e.weight})`
                        : e.weight}
                  </td>
                  <td className="py-1 whitespace-nowrap text-muted-foreground">{e.asOf ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {row.fundamentalsRank != null && (
            <p className="mt-1 text-[11px] text-muted-foreground">
              Tie-break fundamentals rank (growth/margin/ROE z-mean): {row.fundamentalsRank}
            </p>
          )}
        </div>
      )}
    </div>
  )
}

function EvidenceChip({ evidence }: { evidence: QueueEvidence }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Badge
          variant="outline"
          className={cn('px-1.5 py-0 text-[10px]', STATUS_STYLES[evidence.status])}
        >
          {DIMENSION_LABELS[evidence.dimension] ?? evidence.dimension}{' '}
          {STATUS_LABELS[evidence.status]}
        </Badge>
      </TooltipTrigger>
      <TooltipContent className="max-w-sm">
        <div className="font-medium">{evidence.label}</div>
        <div className="mt-0.5">{evidence.observed}</div>
        <div className="text-muted-foreground">Threshold: {evidence.threshold}</div>
        {evidence.note && <div className="mt-0.5 text-warn">{evidence.note}</div>}
      </TooltipContent>
    </Tooltip>
  )
}
