import { useMemo, useState } from 'react'
import {
  flexRender,
  getCoreRowModel,
  getSortedRowModel,
  useReactTable,
  type ColumnDef,
  type SortingState,
} from '@tanstack/react-table'
import { ArrowDown, ArrowUp } from 'lucide-react'
import { useLocation, useNavigate } from 'react-router-dom'
import { cn } from '@/lib/utils'
import { percent, signedPercent } from '@/lib/format'
import type { SectorTickerRow } from '@/lib/types'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'

/// Relative-value grid: raw metric plus a diverging z-score fill (vs the
/// sector median/MAD). Cells without a z (thin sector, no data) stay unfilled.
export function SectorRankingsTable({ rows }: { rows: SectorTickerRow[] }) {
  const [sorting, setSorting] = useState<SortingState>([])
  const navigate = useNavigate()
  const location = useLocation()

  const columns = useMemo<ColumnDef<SectorTickerRow>[]>(
    () => [
      {
        accessorKey: 'ticker',
        header: 'Ticker',
        cell: ({ getValue }) => <span className="font-semibold">{getValue<string>()}</span>,
      },
      {
        accessorKey: 'marketCap',
        header: 'Mkt Cap',
        cell: ({ getValue }) => marketCap(getValue<number | null>()),
        meta: { numeric: true },
      },
      zColumn('pe', 'zPe', 'P/E', (v) => v.toFixed(1)),
      zColumn('revenueGrowthYoY', 'zGrowth', 'Rev Growth', (v) => signedPercent(v, 1)),
      zColumn('grossMargin', 'zGrossMargin', 'Gross Mgn', (v) => percent(v, 1)),
      zColumn('netMargin', 'zNetMargin', 'Net Mgn', (v) => percent(v, 1)),
      zColumn('roe', 'zRoe', 'ROE', (v) => percent(v, 1)),
      zColumn('pctOff52wHigh', 'zPctOff52wHigh', '% Off 52w High', (v) => percent(v, 1), true),
      zColumn('return20d', 'zReturn20d', '20d Return', (v) => signedPercent(v, 1)),
    ],
    [],
  )

  const table = useReactTable({
    data: rows,
    columns,
    state: { sorting },
    onSortingChange: setSorting,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
  })

  return (
    <div className="overflow-x-auto rounded-lg border bg-card">
      <table className="w-full text-sm">
        <thead className="bg-card">
          {table.getHeaderGroups().map((hg) => (
            <tr key={hg.id} className="border-b">
              {hg.headers.map((h) => {
                const numeric = (h.column.columnDef.meta as { numeric?: boolean })?.numeric
                return (
                  <th
                    key={h.id}
                    onClick={h.column.getToggleSortingHandler()}
                    className={cn(
                      'cursor-pointer px-3 py-2 text-xs font-medium text-muted-foreground select-none',
                      numeric ? 'text-right' : 'text-left',
                    )}
                  >
                    <span className="inline-flex items-center gap-1">
                      {flexRender(h.column.columnDef.header, h.getContext())}
                      {h.column.getIsSorted() === 'asc' && <ArrowUp className="size-3" />}
                      {h.column.getIsSorted() === 'desc' && <ArrowDown className="size-3" />}
                    </span>
                  </th>
                )
              })}
            </tr>
          ))}
        </thead>
        <tbody>
          {table.getRowModel().rows.map((row) => (
            <tr
              key={row.id}
              onClick={() =>
                navigate(`/research?ticker=${row.original.ticker}`, {
                  // the search string carries ?sector= so back restores the view
                  state: { back: { path: `${location.pathname}${location.search}`, label: 'Sectors' } },
                })
              }
              className="cursor-pointer border-b border-border/50 transition-colors hover:bg-accent/50"
            >
              {row.getVisibleCells().map((cell) => {
                const numeric = (cell.column.columnDef.meta as { numeric?: boolean })?.numeric
                return (
                  <td key={cell.id} className={cn('px-3 py-1.5', numeric && 'text-right tnum')}>
                    {flexRender(cell.column.columnDef.cell, cell.getContext())}
                  </td>
                )
              })}
            </tr>
          ))}
          {rows.length === 0 && (
            <tr>
              <td colSpan={9} className="px-3 py-6 text-center text-muted-foreground">
                No tickers in this sector yet.
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  )
}

/// A metric column whose cell is tinted by its robust z-score (positive z =
/// green). `zInvert` flips the tint for metrics where a high z is bad; the
/// z itself is already orientation-corrected server-side for P/E.
function zColumn(
  key: keyof SectorTickerRow & string,
  zKey: keyof SectorTickerRow & string,
  header: string,
  format: (v: number) => string,
  zInvert = false,
): ColumnDef<SectorTickerRow> {
  return {
    accessorKey: key,
    header,
    sortUndefined: 'last',
    cell: ({ row, getValue }) => {
      const value = getValue<number | null>()
      const z = row.original[zKey] as number | null
      if (value == null) return <span className="text-muted-foreground">—</span>
      const zShown = z != null ? (zInvert ? -z : z) : null
      const alpha = zShown != null ? Math.min(Math.abs(zShown) / 2.5, 1) * 0.4 : 0
      const backgroundColor =
        zShown == null
          ? undefined
          : zShown >= 0
            ? `rgba(34, 197, 94, ${alpha})`
            : `rgba(239, 68, 68, ${alpha})`
      const cell = (
        <span className="inline-block min-w-14 rounded px-1" style={{ backgroundColor }}>
          {format(value)}
        </span>
      )
      return z == null ? (
        cell
      ) : (
        <Tooltip>
          <TooltipTrigger asChild>{cell}</TooltipTrigger>
          <TooltipContent>z = {z.toFixed(2)} vs sector median (median/MAD)</TooltipContent>
        </Tooltip>
      )
    },
    meta: { numeric: true },
  }
}

function marketCap(millions: number | null): string {
  if (millions == null) return '—'
  if (millions >= 1_000_000) return `$${(millions / 1_000_000).toFixed(2)}T`
  if (millions >= 1_000) return `$${(millions / 1_000).toFixed(1)}B`
  return `$${millions.toFixed(0)}M`
}
