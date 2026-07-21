import { useMemo, useState } from 'react'
import {
  Area,
  AreaChart,
  ResponsiveContainer,
  Tooltip as ChartTooltip,
  XAxis,
  YAxis,
} from 'recharts'
import { cn } from '@/lib/utils'
import { money, signedMoney, signedPercent } from '@/lib/format'
import { Skeleton } from '@/components/ui/skeleton'
import type { HistoryValue, PanelResponse } from '@/lib/types'

const RANGES = [
  { key: '1W', label: '1W', days: 7, caption: 'past week' },
  { key: '1M', label: '1M', days: 30, caption: 'past month' },
  { key: '1Y', label: '1Y', days: 365, caption: 'past year' },
  { key: 'ALL', label: 'All', days: null, caption: 'all time' },
] as const
type RangeKey = (typeof RANGES)[number]['key']

// recharts gets sluggish past ~1k points; all-time daily can be 10k+
const MAX_POINTS = 800

const axisMoney = (v: number) =>
  v >= 10_000 ? `$${(v / 1000).toFixed(0)}k` : v >= 100 ? `$${v.toFixed(0)}` : `$${v.toFixed(2)}`

const fullDate = (d: string) =>
  new Date(d + 'T00:00:00').toLocaleDateString('en-US', {
    weekday: 'short',
    month: 'short',
    day: 'numeric',
    year: 'numeric',
  })

export function PriceHistoryChart({
  resp,
  loading,
  height = 200,
}: {
  resp: PanelResponse<HistoryValue> | undefined
  loading: boolean
  height?: number
}) {
  const [range, setRange] = useState<RangeKey>('1M')
  const def = RANGES.find((r) => r.key === range)!
  const bars = resp?.value?.bars

  const data = useMemo(() => {
    let rows = bars ?? []
    if (def.days != null) {
      const cutoff = new Date()
      cutoff.setDate(cutoff.getDate() - def.days)
      const iso = cutoff.toISOString().slice(0, 10)
      rows = rows.filter((b) => b.date >= iso) // yyyy-MM-dd sorts lexically
    }
    if (rows.length > MAX_POINTS) {
      const stride = Math.ceil(rows.length / MAX_POINTS)
      rows = rows.filter((_, i) => i % stride === 0 || i === rows.length - 1)
    }
    return rows
  }, [bars, def])

  if (loading) return <Skeleton style={{ height: height + 28 }} className="w-full" />

  const change = data.length >= 2 ? data[data.length - 1].close - data[0].close : null
  const changePct =
    change != null && data[0].close > 0 ? (change / data[0].close) * 100 : null
  const up = change == null || change >= 0
  const color = up ? 'var(--gain)' : 'var(--loss)'

  const xTick = (d: string) => {
    const dt = new Date(d + 'T00:00:00')
    if (range === '1W' || range === '1M')
      return dt.toLocaleDateString('en-US', { month: 'short', day: 'numeric' })
    if (range === '1Y') return dt.toLocaleDateString('en-US', { month: 'short' })
    return String(dt.getFullYear())
  }

  return (
    <div>
      <div className="mb-1 flex flex-wrap items-center justify-between gap-2">
        <span className={cn('text-xs font-medium tnum', up ? 'text-gain' : 'text-loss')}>
          {change != null && (
            <>
              {signedMoney(change)} ({signedPercent(changePct)}) {def.caption}
            </>
          )}
        </span>
        <div className="flex gap-1">
          {RANGES.map((r) => (
            <button
              key={r.key}
              onClick={() => setRange(r.key)}
              className={cn(
                'rounded px-2 py-0.5 text-xs transition-colors',
                r.key === range
                  ? 'bg-accent font-medium text-accent-foreground'
                  : 'text-muted-foreground hover:bg-accent/50',
              )}
            >
              {r.label}
            </button>
          ))}
        </div>
      </div>
      {data.length >= 2 ? (
        <ResponsiveContainer width="100%" height={height}>
          <AreaChart data={data} margin={{ top: 4, right: 4, bottom: 0, left: 0 }}>
            <defs>
              <linearGradient id="priceHistFill" x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor={color} stopOpacity={0.25} />
                <stop offset="100%" stopColor={color} stopOpacity={0} />
              </linearGradient>
            </defs>
            <XAxis
              dataKey="date"
              tickFormatter={xTick}
              tick={{ fontSize: 11 }}
              tickLine={false}
              axisLine={false}
              minTickGap={48}
            />
            <YAxis
              domain={['auto', 'auto']}
              tickFormatter={axisMoney}
              tick={{ fontSize: 11 }}
              tickLine={false}
              axisLine={false}
              width={48}
            />
            <ChartTooltip
              cursor={{ stroke: 'var(--border)' }}
              labelFormatter={(d) => (typeof d === 'string' ? fullDate(d) : d)}
              formatter={(v) => [money(v as number), 'Close']}
              contentStyle={{
                background: 'var(--popover)',
                border: '1px solid var(--border)',
                borderRadius: 6,
                fontSize: 12,
                color: 'var(--popover-foreground)',
              }}
            />
            <Area
              type="monotone"
              dataKey="close"
              stroke={color}
              strokeWidth={1.5}
              fill="url(#priceHistFill)"
              dot={false}
              activeDot={{ r: 3 }}
              isAnimationActive={false}
            />
          </AreaChart>
        </ResponsiveContainer>
      ) : (
        <div
          style={{ height }}
          className="flex items-center justify-center text-sm text-muted-foreground"
        >
          {resp ? 'no price history' : ''}
        </div>
      )}
      {resp?.error && <p className="mt-1 text-xs text-warn">{resp.error}</p>}
    </div>
  )
}
