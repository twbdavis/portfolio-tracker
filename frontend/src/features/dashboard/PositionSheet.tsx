import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Pencil, Plus, Trash2 } from 'lucide-react'
import { toast } from 'sonner'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'
import { gainClass, money, shares, shortDate, signedMoney } from '@/lib/format'
import type { LotDto, PositionAccountDetail, PositionDetail, TargetDirection, TickerTarget } from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Switch } from '@/components/ui/switch'
import { Separator } from '@/components/ui/separator'
import { AddLotDialog, EditLotDialog } from '@/features/dashboard/LotDialogs'
import { ConfirmDialog } from '@/components/ConfirmDialog'

/// Row click target: per-account breakdown, lot table with CRUD, sale history,
/// and days-to-long-term per open lot (PositionDetailForm + CumulativeDetailForm).
export function PositionSheet({
  ticker,
  accountId,
  onClose,
}: {
  ticker: string | null
  accountId: number | null
  onClose: () => void
}) {
  const query = useQuery({
    queryKey: ['position-detail', ticker],
    queryFn: () => api.get<PositionDetail>(`/api/positions/${encodeURIComponent(ticker!)}/detail`),
    enabled: ticker !== null,
  })

  const accounts = useMemo(() => {
    const all = query.data?.accounts ?? []
    // an account tab narrows the sheet to that account's lots
    return accountId == null ? all : all.filter((a) => a.accountId === accountId)
  }, [query.data, accountId])

  return (
    <Sheet open={ticker !== null} onOpenChange={(o) => !o && onClose()}>
      <SheetContent side="right" className="w-[560px] overflow-y-auto sm:max-w-[560px]">
        <SheetHeader>
          <SheetTitle className="flex items-center gap-2">
            {ticker}
            {query.data?.name && (
              <span className="text-sm font-normal text-muted-foreground">{query.data.name}</span>
            )}
          </SheetTitle>
          {query.data?.cusip && <SheetDescription>CUSIP {query.data.cusip}</SheetDescription>}
        </SheetHeader>
        <div className="space-y-6 px-4 pb-8">
          {ticker !== null && <TargetSection ticker={ticker} />}
          {accounts.map((acct) => (
            <AccountSection key={acct.positionId} ticker={ticker!} detail={acct} />
          ))}
        </div>
      </SheetContent>
    </Sheet>
  )
}

/// Ticker-level price target + alert (PositionDetailForm's alert panel):
/// shared by every account holding the ticker. Saving a changed price re-arms
/// server-side; Re-arm clears a fired alert without touching the target.
function TargetSection({ ticker }: { ticker: string }) {
  const queryClient = useQueryClient()
  const query = useQuery({
    queryKey: ['ticker-target', ticker],
    queryFn: () => api.get<TickerTarget>(`/api/targets/${encodeURIComponent(ticker)}`),
  })

  const [price, setPrice] = useState('')
  const [direction, setDirection] = useState<TargetDirection>('AtOrAbove')
  const [alertEnabled, setAlertEnabled] = useState(false)
  const [notes, setNotes] = useState('')

  useEffect(() => {
    const t = query.data
    if (t) {
      setPrice(t.targetPrice != null ? String(t.targetPrice) : '')
      setDirection(t.targetDirection)
      setAlertEnabled(t.alertEnabled)
      setNotes(t.notes ?? '')
    }
  }, [query.data])

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ['ticker-target', ticker] })
    queryClient.invalidateQueries({ queryKey: ['position-detail'] })
    queryClient.invalidateQueries({ queryKey: ['dashboard'] })
  }

  const save = useMutation({
    mutationFn: () =>
      api.put(`/api/targets/${encodeURIComponent(ticker)}`, {
        targetPrice: price.trim() === '' ? null : parseFloat(price),
        targetDirection: direction,
        alertEnabled,
        notes: notes.trim() === '' ? null : notes.trim(),
      }),
    onSuccess: () => {
      invalidate()
      toast.success('Target saved')
    },
    onError: (e) => toast.error(String(e)),
  })

  const rearm = useMutation({
    mutationFn: () => api.post(`/api/targets/${encodeURIComponent(ticker)}/rearm`),
    onSuccess: () => {
      invalidate()
      toast.success('Alert re-armed')
    },
    onError: (e) => toast.error(String(e)),
  })

  const remove = useMutation({
    mutationFn: () => api.delete(`/api/targets/${encodeURIComponent(ticker)}`),
    onSuccess: () => {
      invalidate()
      toast.success('Target removed')
    },
    onError: (e) => toast.error(String(e)),
  })

  if (!query.data) return null
  const triggered = query.data.alertTriggeredAt
  const hasTarget = query.data.id !== 0
  const valid = price.trim() === '' || parseFloat(price) > 0

  return (
    <section className="rounded-md border p-3">
      <div className="flex items-center gap-2">
        <h3 className="text-sm font-semibold">Price target</h3>
        {triggered && (
          <Badge variant="outline" className="border-warn text-[10px] text-warn">
            triggered {shortDate(triggered)}
          </Badge>
        )}
      </div>
      <div className="mt-2 grid grid-cols-[7rem_1fr_auto] items-end gap-2">
        <div className="space-y-1">
          <Label htmlFor="target-price" className="text-xs text-muted-foreground">
            Target price
          </Label>
          <Input
            id="target-price"
            type="number"
            step="any"
            min="0"
            className="h-8"
            value={price}
            onChange={(e) => setPrice(e.target.value)}
          />
        </div>
        <div className="space-y-1">
          <Label className="text-xs text-muted-foreground">Direction</Label>
          <Select value={direction} onValueChange={(v) => setDirection(v as TargetDirection)}>
            <SelectTrigger className="h-8">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="AtOrAbove">Alert at or above</SelectItem>
              <SelectItem value="AtOrBelow">Alert at or below</SelectItem>
            </SelectContent>
          </Select>
        </div>
        <div className="flex items-center gap-1.5 pb-1.5">
          <Switch id="target-alert" checked={alertEnabled} onCheckedChange={setAlertEnabled} />
          <Label htmlFor="target-alert" className="text-xs font-normal text-muted-foreground">
            Alert
          </Label>
        </div>
      </div>
      <div className="mt-2 space-y-1">
        <Label htmlFor="target-notes" className="text-xs text-muted-foreground">
          Notes
        </Label>
        <Input
          id="target-notes"
          className="h-8"
          value={notes}
          onChange={(e) => setNotes(e.target.value)}
        />
      </div>
      <div className="mt-2 flex items-center gap-2">
        <Button
          size="sm"
          className="h-7 text-xs"
          onClick={() => save.mutate()}
          disabled={!valid || save.isPending}
        >
          Save target
        </Button>
        {triggered && (
          <Button
            size="sm"
            variant="outline"
            className="h-7 text-xs"
            onClick={() => rearm.mutate()}
            disabled={rearm.isPending}
          >
            Re-arm
          </Button>
        )}
        {hasTarget && (
          <Button
            size="sm"
            variant="ghost"
            className="h-7 text-xs text-destructive"
            onClick={() => remove.mutate()}
            disabled={remove.isPending}
          >
            Remove target
          </Button>
        )}
      </div>
    </section>
  )
}

function AccountSection({ ticker, detail }: { ticker: string; detail: PositionAccountDetail }) {
  const queryClient = useQueryClient()
  const [addOpen, setAddOpen] = useState(false)
  const [editing, setEditing] = useState<LotDto | null>(null)
  const [deleting, setDeleting] = useState<LotDto | null>(null)

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ['position-detail', ticker] })
    queryClient.invalidateQueries({ queryKey: ['dashboard'] })
  }

  const deleteLot = useMutation({
    mutationFn: (txId: number) => api.delete(`/api/lots/${txId}`),
    onSuccess: () => {
      invalidate()
      toast.success('Lot deleted')
    },
    onError: (e) => toast.error(String(e)),
  })

  const openShares = detail.lots.reduce((s, l) => s + l.remainingShares, 0)
  const openCost = detail.lots.reduce((s, l) => s + l.remainingCost, 0)

  return (
    <section>
      <div className="flex items-center gap-2">
        <h3 className="text-sm font-semibold">{detail.account}</h3>
        {detail.isRetirement && (
          <Badge variant="secondary" className="text-[10px]">
            {detail.treatmentLabel}
          </Badge>
        )}
        {detail.needsOpeningBalance && (
          <Badge variant="outline" className="border-warn text-[10px] text-warn">
            needs opening balance
          </Badge>
        )}
        <span className="ml-auto text-xs text-muted-foreground tnum">
          {shares(openShares)} sh · {money(openCost)}
        </span>
      </div>

      <div className="mt-2 rounded-md border">
        <table className="w-full text-xs">
          <thead>
            <tr className="border-b text-muted-foreground">
              <th className="px-2 py-1.5 text-left">Acquired</th>
              <th className="px-2 py-1.5 text-right">Shares</th>
              <th className="px-2 py-1.5 text-right">Remaining</th>
              <th className="px-2 py-1.5 text-right">Cost</th>
              <th className="px-2 py-1.5 text-right">Long-term</th>
              <th className="px-2 py-1.5 text-left">Source</th>
              <th className="w-14 px-2 py-1.5"></th>
            </tr>
          </thead>
          <tbody>
            {detail.lots.length === 0 && (
              <tr>
                <td colSpan={7} className="px-2 py-3 text-center text-muted-foreground">
                  No lots
                </td>
              </tr>
            )}
            {detail.lots.map((lot) => {
              const editable = lot.sourceTxId != null
              return (
                <tr key={lot.id} className="border-b border-border/50 last:border-0">
                  <td className="px-2 py-1 tnum">{shortDate(lot.tradeDate)}</td>
                  <td className="px-2 py-1 text-right tnum">{shares(lot.shares)}</td>
                  <td
                    className={cn(
                      'px-2 py-1 text-right tnum',
                      lot.remainingShares === 0 && 'text-muted-foreground line-through',
                    )}
                  >
                    {shares(lot.remainingShares)}
                  </td>
                  <td className="px-2 py-1 text-right tnum">{money(lot.remainingCost)}</td>
                  <td className="px-2 py-1 text-right tnum">
                    {lot.remainingShares === 0
                      ? '—'
                      : lot.daysToLongTerm == null
                        ? 'long-term'
                        : `in ${lot.daysToLongTerm}d`}
                  </td>
                  <td className="px-2 py-1 text-muted-foreground">
                    {lot.source === 'ChaseImport'
                      ? 'Chase'
                      : lot.source === 'OpeningBalance'
                        ? 'Opening'
                        : 'Manual'}
                  </td>
                  <td className="px-2 py-1 text-right whitespace-nowrap">
                    {editable && (
                      <>
                        <Button
                          variant="ghost"
                          size="icon"
                          className="size-5"
                          onClick={() => setEditing(lot)}
                        >
                          <Pencil className="size-3" />
                        </Button>
                        <Button
                          variant="ghost"
                          size="icon"
                          className="size-5 text-destructive"
                          onClick={() => setDeleting(lot)}
                        >
                          <Trash2 className="size-3" />
                        </Button>
                      </>
                    )}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>

      <div className="mt-1.5">
        <Button variant="ghost" size="sm" className="h-7 text-xs" onClick={() => setAddOpen(true)}>
          <Plus className="size-3" /> Add lot
        </Button>
      </div>

      {detail.sales.length > 0 && (
        <>
          <Separator className="my-3" />
          <h4 className="mb-1.5 text-xs font-medium text-muted-foreground">Sale history</h4>
          <div className="rounded-md border">
            <table className="w-full text-xs">
              <thead>
                <tr className="border-b text-muted-foreground">
                  <th className="px-2 py-1.5 text-left">Sold</th>
                  <th className="px-2 py-1.5 text-right">Shares</th>
                  <th className="px-2 py-1.5 text-right">Proceeds</th>
                  <th className="px-2 py-1.5 text-right">Basis</th>
                  <th className="px-2 py-1.5 text-right">Realized</th>
                </tr>
              </thead>
              <tbody>
                {detail.sales.map((s) => (
                  <tr key={s.id} className="border-b border-border/50 last:border-0">
                    <td className="px-2 py-1 tnum">{shortDate(s.tradeDate)}</td>
                    <td className="px-2 py-1 text-right tnum">
                      {shares(s.sharesSold)}
                      {s.unmatchedShares !== 0 && (
                        <span className="text-warn" title="Shares sold with no lot to consume">
                          {' '}
                          ⚠
                        </span>
                      )}
                    </td>
                    <td className="px-2 py-1 text-right tnum">{money(s.proceeds)}</td>
                    <td className="px-2 py-1 text-right tnum">{money(s.costBasisConsumed)}</td>
                    <td className={cn('px-2 py-1 text-right tnum', gainClass(s.realizedGainLoss))}>
                      {signedMoney(s.realizedGainLoss)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}

      <AddLotDialog
        open={addOpen}
        onOpenChange={setAddOpen}
        defaultAccountId={detail.accountId}
        fixedTicker={ticker}
        openingBalanceHint={detail.needsOpeningBalance}
      />
      <EditLotDialog lot={editing} onClose={() => setEditing(null)} onSaved={invalidate} />
      <ConfirmDialog
        open={deleting !== null}
        title="Delete lot"
        description={
          deleting
            ? `Delete the ${shares(deleting.shares)}-share lot acquired ${shortDate(deleting.tradeDate)}? The position rebuilds from the remaining transactions.`
            : ''
        }
        confirmLabel="Delete"
        destructive
        onConfirm={() => {
          if (deleting?.sourceTxId != null) deleteLot.mutate(deleting.sourceTxId)
          setDeleting(null)
        }}
        onCancel={() => setDeleting(null)}
      />
    </section>
  )
}
