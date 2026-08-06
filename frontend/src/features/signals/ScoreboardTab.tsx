import { useQuery } from '@tanstack/react-query'
import { Info } from 'lucide-react'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'
import { shortDate } from '@/lib/format'
import type { ScoreboardHorizon, ScoreboardResponse } from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'

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

/// Outcome scoreboard: how each signal kind's fires actually played out,
/// measured as excess return vs the sector median frozen at fire time.
/// Informational — small samples show as "insufficient data", never as a
/// confident-looking percentage.
export function ScoreboardTab() {
  const scoreboard = useQuery({
    queryKey: ['scoreboard'],
    queryFn: () => api.get<ScoreboardResponse>('/api/signals/scoreboard'),
  })

  const data = scoreboard.data
  if (scoreboard.isLoading)
    return <div className="p-8 text-center text-sm text-muted-foreground">Loading…</div>

  const rows = (data?.rows ?? []).filter((r) => r.fires > 0)

  return (
    <div className="flex flex-col gap-4 p-4">
      <div className="flex items-start gap-2 rounded-md border border-warn/40 bg-warn/5 px-3 py-2 text-xs text-muted-foreground">
        <Info className="mt-0.5 size-3.5 shrink-0" />
        <span>
          Forward returns measure each fire against its sector&apos;s median return over the same
          window, with membership frozen at fire time. Interpret with care: samples are small,
          horizons overlap, and your own universe choices shape which tickers could fire at all.
          This never turns a signal into a recommendation.
        </span>
      </div>

      {rows.length === 0 ? (
        <div className="rounded-lg border bg-card p-8 text-center text-sm text-muted-foreground">
          No signal fires tracked yet. Outcomes snapshot at fire time and mature at 30/60/90 days
          after each nightly crawl.
        </div>
      ) : (
        <div className="overflow-x-auto rounded-lg border bg-card">
          <table className="w-full text-xs">
            <thead>
              <tr className="border-b text-muted-foreground">
                <th className="px-3 py-2 text-left font-medium">Signal</th>
                <th className="px-2 py-2 text-right font-medium">Fires</th>
                <th className="px-2 py-2 text-right font-medium">
                  <Tooltip>
                    <TooltipTrigger className="underline decoration-dotted">
                      Dismissed
                    </TooltipTrigger>
                    <TooltipContent>
                      How often you dismissed this kind — a proxy for perceived noise.
                    </TooltipContent>
                  </Tooltip>
                </th>
                {[30, 60, 90].map((d) => (
                  <th key={d} className="border-l px-2 py-2 text-center font-medium">
                    {d}d excess vs sector
                  </th>
                ))}
                <th className="border-l px-2 py-2 text-left font-medium">Best / worst (60d)</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => {
                const h60 = row.horizons.find((h) => h.days === 60)
                return (
                  <tr key={row.signalKind} className="border-b border-border/50 last:border-0">
                    <td className="px-3 py-2">
                      <div className="flex items-center gap-1.5 font-medium">
                        {KIND_LABELS[row.signalKind] ?? row.signalKind}
                        {row.downWeighted && (
                          <Tooltip>
                            <TooltipTrigger asChild>
                              <Badge
                                variant="outline"
                                className="px-1.5 py-0 text-[10px] text-warn"
                              >
                                down-weighted
                              </Badge>
                            </TooltipTrigger>
                            <TooltipContent className="max-w-xs">
                              {row.downWeightReason}. Its queue evidence earns half credit while
                              this holds.
                            </TooltipContent>
                          </Tooltip>
                        )}
                      </div>
                    </td>
                    <td className="px-2 py-2 text-right tnum">{row.fires}</td>
                    <td className="px-2 py-2 text-right tnum">
                      {row.dismissRatePct != null
                        ? `${row.dismissed} (${row.dismissRatePct.toFixed(0)}%)`
                        : '—'}
                    </td>
                    {row.horizons.map((h) => (
                      <HorizonCell key={h.days} horizon={h} />
                    ))}
                    <td className="border-l px-2 py-2">
                      {h60?.best && h60.sufficient ? (
                        <span className="tnum">
                          <span className="text-gain">
                            {h60.best.ticker} {fmtPct(h60.best.excess)}
                          </span>{' '}
                          /{' '}
                          <span className="text-loss">
                            {h60.worst!.ticker} {fmtPct(h60.worst!.excess)}
                          </span>{' '}
                          <span className="text-muted-foreground">
                            ({shortDate(h60.best.fireDate)})
                          </span>
                        </span>
                      ) : (
                        <span className="text-muted-foreground">—</span>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

function HorizonCell({ horizon }: { horizon: ScoreboardHorizon }) {
  if (!horizon.sufficient)
    return (
      <td className="border-l px-2 py-2 text-center text-muted-foreground">
        <Tooltip>
          <TooltipTrigger className="underline decoration-dotted">
            insufficient data
          </TooltipTrigger>
          <TooltipContent>
            {horizon.matured} matured observation{horizon.matured === 1 ? '' : 's'} — percentages
            hide below 10 to avoid noise dressed up as a stat.
          </TooltipContent>
        </Tooltip>
      </td>
    )
  return (
    <td className="border-l px-2 py-2 text-center">
      <div
        className={cn(
          'font-medium tnum',
          (horizon.medianExcess ?? 0) >= 0 ? 'text-gain' : 'text-loss',
        )}
      >
        {fmtPct(horizon.medianExcess!)} median
      </div>
      <div className="text-muted-foreground tnum">
        {horizon.beatRatePct!.toFixed(0)}% beat sector · n={horizon.matured}
      </div>
    </td>
  )
}

function fmtPct(v: number) {
  return `${v >= 0 ? '+' : ''}${v.toFixed(1)}%`
}
