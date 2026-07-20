import { percent, signedPercent } from '@/lib/format'
import type { SectorHeatmapRow } from '@/lib/types'
import { cn } from '@/lib/utils'

/// Cross-sector rotation view: one row per sector, one diverging-color cell
/// per rollup metric. Nulls (insufficient coverage) render as plain dashes.
export function SectorHeatmap({
  rows,
  selected,
  onSelect,
}: {
  rows: SectorHeatmapRow[]
  selected: string | null
  onSelect: (sector: string) => void
}) {
  return (
    <div className="overflow-x-auto rounded-lg border bg-card">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b text-xs text-muted-foreground">
            <th className="px-3 py-2 text-left font-medium">Sector</th>
            <th className="px-3 py-2 text-right font-medium">Tickers</th>
            <th className="px-3 py-2 text-right font-medium">Median 20d Return</th>
            <th className="px-3 py-2 text-right font-medium">% Above SMA50</th>
            <th className="px-3 py-2 text-right font-medium">Median % Off 52w High</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr
              key={r.sector}
              onClick={() => onSelect(r.sector)}
              className={cn(
                'cursor-pointer border-b border-border/50 transition-colors hover:bg-accent/50',
                selected === r.sector && 'bg-accent/60',
              )}
            >
              <td className="px-3 py-1.5 font-medium">{r.sector}</td>
              <td className="px-3 py-1.5 text-right tnum text-muted-foreground">{r.tickerCount}</td>
              <HeatCell value={r.medianReturn20d} format={signedPercent} scale={8} />
              <HeatCell
                value={r.breadthAboveSma50}
                format={(v) => percent(v, 0)}
                scale={50}
                center={50}
              />
              <HeatCell
                value={r.medianPctOff52wHigh}
                format={(v) => percent(v, 1)}
                scale={25}
                invert
              />
            </tr>
          ))}
          {rows.length === 0 && (
            <tr>
              <td colSpan={5} className="px-3 py-6 text-center text-muted-foreground">
                Nothing warehoused yet — add tickers to a universe and run a crawl.
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  )
}

/// Diverging green/red fill: distance from `center`, saturating at `scale`.
/// `invert` flips the palette for metrics where high is bad (% off high).
function HeatCell({
  value,
  format,
  scale,
  center = 0,
  invert = false,
}: {
  value: number | null
  format: (v: number) => string
  scale: number
  center?: number
  invert?: boolean
}) {
  if (value == null)
    return <td className="px-3 py-1.5 text-right tnum text-muted-foreground">—</td>
  let v = value - center
  if (invert) v = -v
  const alpha = Math.min(Math.abs(v) / scale, 1) * 0.45
  const backgroundColor =
    v >= 0 ? `rgba(34, 197, 94, ${alpha})` : `rgba(239, 68, 68, ${alpha})`
  return (
    <td className="px-3 py-1.5 text-right tnum" style={{ backgroundColor }}>
      {format(value)}
    </td>
  )
}
