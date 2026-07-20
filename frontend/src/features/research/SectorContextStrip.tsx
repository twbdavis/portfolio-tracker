import { useQuery } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'
import { shortDate } from '@/lib/format'
import type { SectorContextResponse, SignalHitDto } from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'

export function sectorContextQuery(ticker: string) {
  return {
    queryKey: ['research', ticker, 'sector-context'],
    queryFn: () =>
      api.get<SectorContextResponse>(
        `/api/research/${encodeURIComponent(ticker)}/sector-context`,
      ),
    staleTime: 10 * 60_000,
    enabled: ticker.length > 0,
  }
}

/// "vs sector" strip under fundamentals: each warehoused metric with the
/// sector median and a small percentile bar. Renders nothing until the
/// crawler has classified the ticker — sector context is warehouse-only.
export function SectorContextStrip({ ticker }: { ticker: string }) {
  const ctx = useQuery(sectorContextQuery(ticker))
  const data = ctx.data
  if (!data?.sector || data.coverageCount < 2) return null

  const withValues = data.metrics.filter((m) => m.value != null)
  if (withValues.length === 0) return null

  return (
    <div className="rounded-lg border bg-card p-3">
      <div className="mb-2 flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
        <span className="text-[11px] font-medium tracking-wider text-muted-foreground uppercase">
          vs {data.sector}
        </span>
        <span className="text-xs text-muted-foreground">
          {data.coverageCount} tickers warehoused
          {data.asOf && ` · as of ${shortDate(data.asOf)}`}
        </span>
      </div>
      <div className="grid grid-cols-2 gap-x-6 gap-y-1.5 md:grid-cols-3">
        {withValues.map((m) => (
          <div key={m.key}>
            <div className="flex items-baseline justify-between text-xs">
              <span className="text-muted-foreground">{m.label}</span>
              <span className="tnum">
                {fmt(m.value!, m.isPercent)}
                <span className="text-muted-foreground">
                  {' '}
                  / med {m.sectorMedian == null ? '—' : fmt(m.sectorMedian, m.isPercent)}
                </span>
              </span>
            </div>
            <PercentileBar percentile={m.percentile} />
          </div>
        ))}
      </div>
    </div>
  )
}

/// Where this ticker sits in its sector, 0..100. Neutral color on purpose —
/// whether a high percentile is good depends on the metric.
function PercentileBar({ percentile }: { percentile: number | null }) {
  if (percentile == null) return <div className="mt-1 h-1.5 rounded-full bg-muted/60" />
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <div className="mt-1 h-1.5 rounded-full bg-muted/60">
          <div
            className="h-full rounded-full bg-primary/70"
            style={{ width: `${Math.min(Math.max(percentile, 2), 100)}%` }}
          />
        </div>
      </TooltipTrigger>
      <TooltipContent>{percentile.toFixed(0)}th percentile within sector</TooltipContent>
    </Tooltip>
  )
}

/// Active (undismissed) signal badges for the researched ticker, linking to
/// the /signals feed where the full why-panels live.
export function SignalBadges({ ticker }: { ticker: string }) {
  const navigate = useNavigate()
  const hits = useQuery<SignalHitDto[]>({
    queryKey: ['signals', 'ticker', ticker],
    queryFn: () => api.get<SignalHitDto[]>(`/api/signals/ticker/${encodeURIComponent(ticker)}`),
    staleTime: 60_000,
    enabled: ticker.length > 0,
  })
  if (!hits.data?.length) return null
  return (
    <>
      {hits.data.map((h) => (
        <Tooltip key={h.id}>
          <TooltipTrigger asChild>
            <button onClick={() => navigate('/signals')}>
              <Badge
                variant="outline"
                className={cn(
                  'cursor-pointer text-[10px]',
                  h.signalKind === 'DeathCross' ? 'border-loss/50 text-loss' : 'border-gain/50 text-gain',
                )}
              >
                {h.signalKind}
              </Badge>
            </button>
          </TooltipTrigger>
          <TooltipContent className="max-w-sm">{h.summary}</TooltipContent>
        </Tooltip>
      ))}
    </>
  )
}

function fmt(v: number, isPercent: boolean): string {
  return isPercent ? `${v.toFixed(1)}%` : v.toFixed(2)
}
