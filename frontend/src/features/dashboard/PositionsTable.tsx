import { useMemo, useState } from 'react'
import {
  flexRender,
  getCoreRowModel,
  getSortedRowModel,
  useReactTable,
  type ColumnDef,
  type SortingState,
} from '@tanstack/react-table'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, MoreHorizontal } from 'lucide-react'
import { useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'
import { gainClass, money, percent, shares, signedMoney, signedPercent } from '@/lib/format'
import { useLive } from '@/lib/signalr'
import type { PositionRow } from '@/lib/types'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { SellSimulatorDialog } from '@/features/simulator/SellSimulatorDialog'
import { ConfirmDialog } from '@/components/ConfirmDialog'

function alertStatus(row: PositionRow): { text: string; triggered: boolean } {
  if (row.targetPrice == null) return { text: '', triggered: false }
  if (row.alertTriggeredAt) return { text: 'TRIGGERED', triggered: true }
  return { text: row.alertEnabled ? 'armed' : 'off', triggered: false }
}

export function PositionsTable({
  rows,
  accountId,
  onOpen,
}: {
  rows: PositionRow[]
  accountId: number | null
  onOpen: (ticker: string) => void
}) {
  const [sorting, setSorting] = useState<SortingState>([])
  const { flashes } = useLive()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [simulate, setSimulate] = useState<PositionRow | null>(null)
  const [deleting, setDeleting] = useState<PositionRow | null>(null)

  const deletePosition = useMutation({
    mutationFn: (row: PositionRow) =>
      row.positionId > 0
        ? api.delete(`/api/positions/${row.positionId}`)
        : api.delete(`/api/positions/ticker/${encodeURIComponent(row.ticker)}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['dashboard'] })
      toast.success('Position deleted')
    },
    onError: (e) => toast.error(String(e)),
  })

  const columns = useMemo<ColumnDef<PositionRow>[]>(
    () => [
      {
        accessorKey: 'ticker',
        header: 'Ticker',
        cell: ({ row }) => {
          const r = row.original
          const cell = (
            <span
              className={cn(
                'font-semibold',
                r.needsOpeningBalance
                  ? 'text-warn'
                  : r.priceUnavailable && r.shares !== 0
                    ? 'text-muted-foreground'
                    : '',
              )}
            >
              {r.ticker}
              {r.needsOpeningBalance && ' ⚠'}
              {r.heldInIra && ' ◆'}
            </span>
          )
          return r.iraNote ? (
            <Tooltip>
              <TooltipTrigger asChild>{cell}</TooltipTrigger>
              <TooltipContent>{r.iraNote}</TooltipContent>
            </Tooltip>
          ) : (
            cell
          )
        },
      },
      {
        accessorKey: 'shares',
        header: 'Shares',
        cell: ({ getValue }) => shares(getValue<number>()),
        meta: { numeric: true },
      },
      {
        accessorKey: 'avgCost',
        header: 'Avg Cost',
        cell: ({ getValue }) => money(getValue<number | null>()),
        meta: { numeric: true },
      },
      {
        accessorKey: 'currentPrice',
        header: 'Price',
        cell: ({ row, getValue }) => {
          const flash = flashes.get(row.original.ticker)
          return (
            <span
              className={cn(
                'inline-block rounded px-1',
                flash === 'up' && 'quote-flash-up',
                flash === 'down' && 'quote-flash-down',
              )}
            >
              {money(getValue<number | null>())}
            </span>
          )
        },
        meta: { numeric: true },
      },
      {
        accessorKey: 'marketValue',
        header: 'Mkt Value',
        cell: ({ getValue }) => money(getValue<number | null>()),
        meta: { numeric: true },
      },
      {
        accessorKey: 'dayGainLoss',
        header: 'Daily Gain/Loss $',
        cell: ({ getValue }) => (
          <span className={gainClass(getValue<number | null>())}>
            {signedMoney(getValue<number | null>())}
          </span>
        ),
        meta: { numeric: true },
      },
      {
        accessorKey: 'gainLoss',
        header: 'Total Gain/Loss $',
        cell: ({ getValue }) => (
          <span className={gainClass(getValue<number | null>())}>
            {signedMoney(getValue<number | null>())}
          </span>
        ),
        meta: { numeric: true },
      },
      {
        accessorKey: 'gainLossPct',
        header: 'Total Gain/Loss %',
        cell: ({ getValue }) => (
          <span className={gainClass(getValue<number | null>())}>
            {signedPercent(getValue<number | null>())}
          </span>
        ),
        meta: { numeric: true },
      },
      {
        accessorKey: 'targetPrice',
        header: 'Target',
        cell: ({ getValue }) => money(getValue<number | null>()),
        meta: { numeric: true },
      },
      {
        accessorKey: 'distanceToTargetPct',
        header: 'To Target %',
        cell: ({ getValue }) => percent(getValue<number | null>()),
        meta: { numeric: true },
      },
      {
        id: 'alert',
        header: 'Alert',
        accessorFn: (r) => alertStatus(r).text,
        cell: ({ row }) => {
          const s = alertStatus(row.original)
          return (
            <span className={cn('text-xs', s.triggered && 'font-semibold text-warn')}>
              {s.text}
            </span>
          )
        },
      },
      {
        id: 'actions',
        header: '',
        enableSorting: false,
        cell: ({ row }) => (
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button
                variant="ghost"
                size="icon"
                className="size-6"
                onClick={(e) => e.stopPropagation()}
              >
                <MoreHorizontal className="size-4" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" onClick={(e) => e.stopPropagation()}>
              <DropdownMenuItem onClick={() => onOpen(row.original.ticker)}>
                Edit position…
              </DropdownMenuItem>
              <DropdownMenuItem
                onClick={() => navigate(`/research?ticker=${row.original.ticker}`)}
              >
                Research this ticker
              </DropdownMenuItem>
              <DropdownMenuItem onClick={() => setSimulate(row.original)}>
                What if I sell…
              </DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem
                className="text-destructive"
                onClick={() => setDeleting(row.original)}
              >
                Delete position…
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        ),
      },
    ],
    [flashes, navigate, onOpen],
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
    <>
      <table className="w-full text-sm">
        <thead className="sticky top-0 z-10 bg-card">
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
              onClick={() => onOpen(row.original.ticker)}
              className="cursor-pointer border-b border-border/50 transition-colors hover:bg-accent/50"
            >
              {row.getVisibleCells().map((cell) => {
                const numeric = (cell.column.columnDef.meta as { numeric?: boolean })?.numeric
                return (
                  <td
                    key={cell.id}
                    className={cn('px-3 py-1.5', numeric && 'text-right tnum')}
                  >
                    {flexRender(cell.column.columnDef.cell, cell.getContext())}
                  </td>
                )
              })}
            </tr>
          ))}
        </tbody>
      </table>

      <SellSimulatorDialog
        row={simulate}
        accountId={accountId}
        onClose={() => setSimulate(null)}
      />
      <ConfirmDialog
        open={deleting !== null}
        title={deleting ? `Delete ${deleting.ticker}` : ''}
        description="This deletes the position including all of its lots, sales, and realized history. Transactions imported from Chase CSVs are deleted too; re-importing the CSV will bring them back."
        confirmLabel="Delete"
        destructive
        onConfirm={() => {
          if (deleting) deletePosition.mutate(deleting)
          setDeleting(null)
        }}
        onCancel={() => setDeleting(null)}
      />
    </>
  )
}
