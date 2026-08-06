import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronDown, ChevronRight, Info, RefreshCw, X } from 'lucide-react'
import { useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'
import { shortDate, timeOfDay } from '@/lib/format'
import type { SignalHitDto } from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { ScoreboardTab } from '@/features/signals/ScoreboardTab'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'

const KIND_LABELS: Record<string, string> = {
  QualityOnSale: 'Quality on sale',
  CheapGrowth: 'Cheap growth',
  RecMomentum: 'Recommendation momentum',
  GoldenCross: 'Golden cross',
  DeathCross: 'Death cross',
  SectorDivergence: 'Sector divergence',
  CatalystWatch: 'Catalyst watch',
  BreadthThrust: 'Breadth thrust (sector)',
}

/// Signals home: the live feed of rule hits plus the outcome scoreboard.
export function SignalsPage() {
  return (
    <Tabs defaultValue="feed">
      <div className="flex items-center gap-3 border-b px-4 pt-3 pb-2">
        <h1 className="text-lg font-semibold">Signals</h1>
        <TabsList>
          <TabsTrigger value="feed">Feed</TabsTrigger>
          <TabsTrigger value="scoreboard">Scoreboard</TabsTrigger>
        </TabsList>
      </div>
      <TabsContent value="feed">
        <SignalsFeed />
      </TabsContent>
      <TabsContent value="scoreboard">
        <ScoreboardTab />
      </TabsContent>
    </Tabs>
  )
}

/// The opportunity signals feed: rule hits grouped by kind, each with its
/// full "why this fired" arithmetic. Signals never notify by default — this
/// feed is their home.
function SignalsFeed() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [sectorFilter, setSectorFilter] = useState<string>('all')
  const [kindFilter, setKindFilter] = useState<string>('all')
  const [sortBy, setSortBy] = useState<'strength' | 'newest'>('strength')

  const signals = useQuery<SignalHitDto[]>({
    queryKey: ['signals'],
    queryFn: () => api.get<SignalHitDto[]>('/api/signals'),
  })

  const evaluate = useMutation({
    mutationFn: () => api.post<{ fired: number }>('/api/signals/evaluate'),
    onSuccess: (r) => {
      toast.success(r.fired > 0 ? `${r.fired} new signal${r.fired === 1 ? '' : 's'}` : 'No new signals')
      queryClient.invalidateQueries({ queryKey: ['signals'] })
    },
    onError: (e) => toast.error(String(e)),
  })

  const dismiss = useMutation({
    mutationFn: (id: number) => api.post(`/api/signals/${id}/dismiss`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['signals'] }),
    onError: (e) => toast.error(String(e)),
  })

  const addToWatchlist = useMutation({
    mutationFn: (hit: SignalHitDto) =>
      api.post('/api/watchlist', {
        ticker: hit.ticker,
        name: null,
        thesis: `[Machine-generated starting point from the ${KIND_LABELS[hit.signalKind] ?? hit.signalKind} screening signal — verify before acting]\n${hit.summary}`,
        conviction: 3,
        targetBuyPrice: null,
        alertEnabled: false,
      }),
    onSuccess: (_, hit) => toast.success(`${hit.ticker} added to watchlist`),
    onError: (e) => toast.error(String(e)),
  })

  const sectors = useMemo(
    () =>
      Array.from(new Set((signals.data ?? []).map((s) => s.sector).filter(Boolean) as string[])).sort(),
    [signals.data],
  )
  const kinds = useMemo(
    () => Array.from(new Set((signals.data ?? []).map((s) => s.signalKind))).sort(),
    [signals.data],
  )
  const filtered = (signals.data ?? []).filter(
    (s) =>
      (sectorFilter === 'all' || s.sector === sectorFilter) &&
      (kindFilter === 'all' || s.signalKind === kindFilter),
  )
  // group by kind; hits sort within their group, groups order by their best
  // hit so the strongest cluster is always on top
  const byKind = useMemo(() => {
    const compare = (a: SignalHitDto, b: SignalHitDto) =>
      sortBy === 'strength'
        ? (b.strength ?? -1) - (a.strength ?? -1) ||
          new Date(b.firedAt).getTime() - new Date(a.firedAt).getTime()
        : new Date(b.firedAt).getTime() - new Date(a.firedAt).getTime()
    const groups = new Map<string, SignalHitDto[]>()
    for (const s of filtered) {
      const list = groups.get(s.signalKind) ?? []
      list.push(s)
      groups.set(s.signalKind, list)
    }
    return Array.from(groups.entries())
      .map(([kind, hits]) => [kind, [...hits].sort(compare)] as const)
      .sort(([, a], [, b]) => compare(a[0], b[0]))
  }, [filtered, sortBy])

  return (
    <div className="flex flex-col gap-4 p-4">
      <div className="flex flex-wrap items-center justify-end gap-2">
        <div className="flex flex-wrap items-center gap-2">
          <Select value={kindFilter} onValueChange={setKindFilter}>
            <SelectTrigger className="w-56">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All signal types</SelectItem>
              {kinds.map((k) => (
                <SelectItem key={k} value={k}>
                  {KIND_LABELS[k] ?? k}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Select value={sectorFilter} onValueChange={setSectorFilter}>
            <SelectTrigger className="w-48">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All sectors</SelectItem>
              {sectors.map((s) => (
                <SelectItem key={s} value={s}>
                  {s}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Select value={sortBy} onValueChange={(v) => setSortBy(v as 'strength' | 'newest')}>
            <SelectTrigger className="w-40">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="strength">By strength</SelectItem>
              <SelectItem value="newest">Newest first</SelectItem>
            </SelectContent>
          </Select>
          <Button
            variant="outline"
            size="sm"
            onClick={() => evaluate.mutate()}
            disabled={evaluate.isPending}
          >
            <RefreshCw className={cn('size-4', evaluate.isPending && 'animate-spin')} />
            Re-evaluate signals
          </Button>
        </div>
      </div>

      <div className="flex items-start gap-2 rounded-md border border-warn/40 bg-warn/5 px-3 py-2 text-xs text-muted-foreground">
        <Info className="mt-0.5 size-3.5 shrink-0" />
        <span>
          Signals are screening heuristics computed from limited free data — not predictions,
          not investment advice. No signal implies an expected return; every firing shows its
          arithmetic so you can judge it yourself.
        </span>
      </div>

      {byKind.length === 0 && (
        <div className="rounded-lg border bg-card p-8 text-center text-sm text-muted-foreground">
          No active signals. Signals evaluate automatically after each nightly crawl, or use
          Re-evaluate now.
        </div>
      )}

      {byKind.map(([kind, hits]) => (
        <div key={kind}>
          <h2 className="mb-2 text-sm font-semibold">
            {KIND_LABELS[kind] ?? kind}{' '}
            <span className="font-normal text-muted-foreground">({hits.length})</span>
          </h2>
          <div className="flex flex-col gap-2">
            {hits.map((hit) => (
              <SignalCard
                key={hit.id}
                hit={hit}
                onDismiss={() => dismiss.mutate(hit.id)}
                onResearch={() =>
                  navigate(`/research?ticker=${hit.ticker}`, {
                    state: { back: { path: '/signals', label: 'Signals' } },
                  })
                }
                onWatchlist={() => addToWatchlist.mutate(hit)}
              />
            ))}
          </div>
        </div>
      ))}
    </div>
  )
}

/// 0-100 margin beyond the rule's thresholds. Color tiers keep it scannable;
/// the tooltip repeats what it is NOT (a return forecast).
function StrengthBadge({ strength }: { strength: number | null }) {
  if (strength == null) return null
  const tier =
    strength >= 67
      ? 'border-gain/60 text-gain'
      : strength >= 34
        ? 'border-warn/60 text-warn'
        : 'border-border text-muted-foreground'
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Badge variant="outline" className={cn('px-1.5 py-0 text-[10px] tnum', tier)}>
          {strength}
        </Badge>
      </TooltipTrigger>
      <TooltipContent className="max-w-xs">
        Signal strength {strength}/100 — how far past its thresholds this rule fired, for ranking
        hits against each other. It is not a predicted return.
      </TooltipContent>
    </Tooltip>
  )
}

function SignalCard({
  hit,
  onDismiss,
  onResearch,
  onWatchlist,
}: {
  hit: SignalHitDto
  onDismiss: () => void
  onResearch: () => void
  onWatchlist: () => void
}) {
  const [open, setOpen] = useState(false)
  const sectorLevel = hit.signalKind === 'BreadthThrust'

  return (
    <div className="rounded-lg border bg-card p-3">
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-semibold">{hit.ticker}</span>
            <StrengthBadge strength={hit.strength} />
            {hit.sector && !sectorLevel && (
              <Badge variant="secondary" className="px-1.5 py-0 text-[10px]">
                {hit.sector}
              </Badge>
            )}
            <span className="text-xs text-muted-foreground">
              {shortDate(hit.firedAt)} {timeOfDay(hit.firedAt)}
            </span>
          </div>
          <p className="mt-1 text-sm">{hit.summary}</p>
        </div>
        <div className="flex shrink-0 items-center gap-1">
          {!sectorLevel && (
            <>
              <Button variant="ghost" size="sm" className="h-7 px-2 text-xs" onClick={onResearch}>
                Research
              </Button>
              <Button variant="ghost" size="sm" className="h-7 px-2 text-xs" onClick={onWatchlist}>
                Watch
              </Button>
            </>
          )}
          <Button variant="ghost" size="icon" className="size-7" onClick={onDismiss}>
            <X className="size-3.5" />
          </Button>
        </div>
      </div>

      <button
        className="mt-2 flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
        onClick={() => setOpen(!open)}
      >
        {open ? <ChevronDown className="size-3" /> : <ChevronRight className="size-3" />}
        Why this fired
      </button>
      {open && (
        <table className="mt-2 w-full text-xs">
          <thead>
            <tr className="border-b text-muted-foreground">
              <th className="py-1 pr-2 text-left font-medium">Condition</th>
              <th className="py-1 pr-2 text-left font-medium">Observed</th>
              <th className="py-1 pr-2 text-left font-medium">Threshold</th>
              <th className="py-1 text-left font-medium">Result</th>
            </tr>
          </thead>
          <tbody>
            {hit.conditions.map((c, i) => (
              <tr key={i} className="border-b border-border/50">
                <td className="py-1 pr-2">{c.label}</td>
                <td className="py-1 pr-2 tnum">{c.observed}</td>
                <td className="py-1 pr-2 tnum">{c.threshold}</td>
                <td className={cn('py-1 font-medium', c.pass ? 'text-gain' : 'text-loss')}>
                  {c.pass ? 'pass' : 'fail'}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
