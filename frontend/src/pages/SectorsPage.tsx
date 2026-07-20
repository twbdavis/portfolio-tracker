import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Boxes } from 'lucide-react'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'
import { gainClass, percent, shortDate, signedPercent, timeOfDay } from '@/lib/format'
import type { SectorHeatmapRow, SectorInfo, SectorSummary, SectorTickerRow } from '@/lib/types'
import { Button } from '@/components/ui/button'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { CrawlerStatusWidget } from '@/features/sectors/CrawlerStatusWidget'
import { SectorHeatmap } from '@/features/sectors/SectorHeatmap'
import { SectorRankingsTable } from '@/features/sectors/SectorRankingsTable'
import { UniverseManagerDialog } from '@/features/sectors/UniverseManagerDialog'

/// Sector dashboard: everything renders from the local warehouse with an
/// as-of timestamp — a stale sector shows a refresh affordance (the crawler
/// widget), never a spinner waiting on a live API.
export function SectorsPage() {
  const [selected, setSelected] = useState<string | null>(null)
  const [universesOpen, setUniversesOpen] = useState(false)

  const sectors = useQuery<SectorInfo[]>({
    queryKey: ['sectors'],
    queryFn: () => api.get<SectorInfo[]>('/api/sectors'),
  })
  const heatmap = useQuery<SectorHeatmapRow[]>({
    queryKey: ['sectors', 'heatmap'],
    queryFn: () => api.get<SectorHeatmapRow[]>('/api/sectors/heatmap'),
  })

  const sector = selected ?? sectors.data?.find((s) => s.sector !== 'Unclassified')?.sector ?? null

  const summary = useQuery<SectorSummary>({
    queryKey: ['sectors', sector, 'summary'],
    queryFn: () => api.get<SectorSummary>(`/api/sectors/${encodeURIComponent(sector!)}/summary`),
    enabled: sector != null,
  })
  const rankings = useQuery<SectorTickerRow[]>({
    queryKey: ['sectors', sector, 'rankings'],
    queryFn: () =>
      api.get<SectorTickerRow[]>(`/api/sectors/${encodeURIComponent(sector!)}/rankings`),
    enabled: sector != null,
  })

  const asOf = summary.data?.newestMetricsAsOf ?? null
  const stale = useMemo(
    () => asOf != null && Date.now() - new Date(asOf).getTime() > 36 * 3600 * 1000,
    [asOf],
  )

  return (
    <div className="flex flex-col gap-4 p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="text-lg font-semibold">Sectors</h1>
        <div className="flex items-center gap-2">
          <CrawlerStatusWidget />
          <Button variant="outline" size="sm" onClick={() => setUniversesOpen(true)}>
            <Boxes className="size-4" /> Universes
          </Button>
        </div>
      </div>

      <SectorHeatmap rows={heatmap.data ?? []} selected={sector} onSelect={setSelected} />

      <div className="flex flex-wrap items-center gap-3">
        <Select value={sector ?? undefined} onValueChange={setSelected}>
          <SelectTrigger className="w-72">
            <SelectValue placeholder="Pick a sector…" />
          </SelectTrigger>
          <SelectContent>
            {sectors.data?.map((s) => (
              <SelectItem key={s.sector} value={s.sector}>
                {s.sector} ({s.withMetrics}/{s.tickerCount} crawled)
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        {asOf && (
          <span className={cn('text-xs', stale ? 'text-warn' : 'text-muted-foreground')}>
            data as of {shortDate(asOf)} {timeOfDay(asOf)}
            {stale && ' — stale, refresh via the crawler'}
          </span>
        )}
      </div>

      {summary.data && (
        <>
          <div className="grid grid-cols-2 gap-3 md:grid-cols-3 lg:grid-cols-6">
            <StatCard label="Median P/E" value={summary.data.medianPe?.toFixed(1) ?? '—'} />
            <StatCard label="Median P/S" value={summary.data.medianPs?.toFixed(2) ?? '—'} />
            <StatCard label="Median P/B" value={summary.data.medianPb?.toFixed(2) ?? '—'} />
            <StatCard
              label="Median Rev Growth"
              value={signedPercent(summary.data.medianRevenueGrowth, 1)}
              valueClass={gainClass(summary.data.medianRevenueGrowth)}
            />
            <StatCard
              label="Median 20d Return"
              value={signedPercent(summary.data.medianReturn20d, 1)}
              valueClass={gainClass(summary.data.medianReturn20d)}
            />
            <StatCard
              label="Avg % Off 52w High"
              value={percent(summary.data.avgPctOff52wHigh, 1)}
            />
          </div>

          <div className="grid gap-3 md:grid-cols-2">
            <BreadthGauge label="Above SMA50" value={summary.data.breadthAboveSma50} />
            <BreadthGauge label="Above SMA200" value={summary.data.breadthAboveSma200} />
          </div>

          <div className="grid gap-3 md:grid-cols-2">
            <MoversCard title="Top 5 by 20d return" movers={summary.data.top5By20d} />
            <MoversCard title="Bottom 5 by 20d return" movers={summary.data.bottom5By20d} />
          </div>
        </>
      )}

      <SectorRankingsTable rows={rankings.data ?? []} />

      <UniverseManagerDialog open={universesOpen} onOpenChange={setUniversesOpen} />
    </div>
  )
}

function StatCard({
  label,
  value,
  valueClass,
}: {
  label: string
  value: string
  valueClass?: string
}) {
  return (
    <div className="rounded-lg border bg-card p-3">
      <div className="text-[11px] font-medium tracking-wider text-muted-foreground uppercase">
        {label}
      </div>
      <div className={cn('text-lg font-semibold tnum', valueClass)}>{value}</div>
    </div>
  )
}

/// Breadth as a simple bar: share of the sector trading above its moving
/// average. Null (insufficient history) renders as an empty track.
function BreadthGauge({ label, value }: { label: string; value: number | null }) {
  return (
    <div className="rounded-lg border bg-card p-3">
      <div className="mb-1.5 flex items-baseline justify-between">
        <span className="text-[11px] font-medium tracking-wider text-muted-foreground uppercase">
          {label}
        </span>
        <span className="text-sm font-semibold tnum">{percent(value, 0)}</span>
      </div>
      <div className="h-2 overflow-hidden rounded-full bg-muted">
        {value != null && (
          <div
            className={cn('h-full rounded-full', value >= 50 ? 'bg-gain' : 'bg-loss')}
            style={{ width: `${Math.min(Math.max(value, 0), 100)}%` }}
          />
        )}
      </div>
    </div>
  )
}

function MoversCard({
  title,
  movers,
}: {
  title: string
  movers: { ticker: string; return20d: number }[]
}) {
  return (
    <div className="rounded-lg border bg-card p-3">
      <div className="mb-1.5 text-[11px] font-medium tracking-wider text-muted-foreground uppercase">
        {title}
      </div>
      <div className="flex flex-col gap-1 text-sm">
        {movers.map((m) => (
          <div key={m.ticker} className="flex items-baseline justify-between">
            <span className="font-medium">{m.ticker}</span>
            <span className={cn('tnum', gainClass(m.return20d))}>
              {signedPercent(m.return20d, 1)}
            </span>
          </div>
        ))}
        {movers.length === 0 && <span className="text-muted-foreground">No return data yet.</span>}
      </div>
    </div>
  )
}
