import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { api } from '@/lib/api'
import { shares as fmtShares } from '@/lib/format'
import type { WatchlistItem } from '@/lib/types'
import { useAccounts } from '@/hooks/useAccounts'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { Textarea } from '@/components/ui/textarea'

const CONVICTIONS = [
  '1 — speculative',
  '2 — leaning in',
  '3 — solid case',
  '4 — strong case',
  '5 — table-pounding',
]

/// Add/edit one watchlist entry: thesis, conviction, optional buy target
/// (WatchlistEntryForm). Changing the target buy price re-arms server-side.
export function WatchlistEntryDialog({
  open,
  onOpenChange,
  existing,
  fixedTicker,
  name,
}: {
  open: boolean
  onOpenChange: (o: boolean) => void
  existing: WatchlistItem | null
  fixedTicker?: string
  name?: string | null
}) {
  const queryClient = useQueryClient()
  const [ticker, setTicker] = useState('')
  const [thesis, setThesis] = useState('')
  const [conviction, setConviction] = useState('3')
  const [targetText, setTargetText] = useState('')
  const [alertEnabled, setAlertEnabled] = useState(true)

  useEffect(() => {
    if (open) {
      setTicker(existing?.ticker ?? fixedTicker ?? '')
      setThesis(existing?.thesis ?? '')
      setConviction(String(existing?.conviction ?? 3))
      setTargetText(existing?.targetBuyPrice != null ? String(existing.targetBuyPrice) : '')
      setAlertEnabled(existing?.alertEnabled ?? true)
    }
  }, [open, existing, fixedTicker])

  const save = useMutation({
    mutationFn: () => {
      const target = targetText.trim() === '' ? null : parseFloat(targetText)
      const body = {
        thesis: thesis.trim() === '' ? null : thesis.trim(),
        conviction: Number(conviction),
        targetBuyPrice: target,
        alertEnabled: alertEnabled && target != null,
      }
      return existing
        ? api.put(`/api/watchlist/${existing.id}`, body)
        : api.post('/api/watchlist', {
            ...body,
            ticker: ticker.trim().toUpperCase(),
            name: name ?? null,
          })
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['watchlist'] })
      onOpenChange(false)
    },
    onError: (e) => toast.error(String(e)),
  })

  const hasTarget = targetText.trim() !== ''
  const valid =
    (existing != null || ticker.trim() !== '') && (!hasTarget || parseFloat(targetText) > 0)

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>
            {existing
              ? `Edit ${existing.ticker} watchlist entry`
              : fixedTicker
                ? `Add ${fixedTicker} to watchlist`
                : 'Add to watchlist'}
          </DialogTitle>
          <DialogDescription>
            Watched tickers never count toward portfolio totals.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          {existing == null && fixedTicker == null && (
            <div className="space-y-1.5">
              <Label htmlFor="watch-ticker">Ticker</Label>
              <Input
                id="watch-ticker"
                value={ticker}
                onChange={(e) => setTicker(e.target.value.toUpperCase())}
              />
            </div>
          )}
          <div className="space-y-1.5">
            <Label htmlFor="watch-thesis">Why would you buy this?</Label>
            <Textarea
              id="watch-thesis"
              rows={4}
              value={thesis}
              onChange={(e) => setThesis(e.target.value)}
            />
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <Label>Conviction</Label>
              <Select value={conviction} onValueChange={setConviction}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {CONVICTIONS.map((label, i) => (
                    <SelectItem key={label} value={String(i + 1)}>
                      {label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="watch-target">Target buy price</Label>
              <Input
                id="watch-target"
                type="number"
                step="any"
                min="0"
                value={targetText}
                onChange={(e) => setTargetText(e.target.value)}
              />
            </div>
          </div>
          <div className="flex items-center gap-2 pt-1">
            <Switch
              id="watch-alert"
              checked={alertEnabled && hasTarget}
              disabled={!hasTarget}
              onCheckedChange={setAlertEnabled}
            />
            <Label htmlFor="watch-alert" className="text-sm font-normal">
              Alert when price ≤ target
            </Label>
          </div>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button onClick={() => save.mutate()} disabled={!valid || save.isPending}>
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

/// "I bought it": records the lot (wash warning surfaces as a long toast),
/// then — like the WinForms flow — asks whether to retire the watchlist row.
export function BoughtDialog({
  item,
  onClose,
}: {
  item: WatchlistItem | null
  onClose: () => void
}) {
  const { data: accounts } = useAccounts()
  const queryClient = useQueryClient()
  const [accountId, setAccountId] = useState('')
  const [sharesText, setSharesText] = useState('')
  const [costText, setCostText] = useState('')
  const [date, setDate] = useState('')
  const [askKeep, setAskKeep] = useState<{ id: number; ticker: string; shares: number } | null>(
    null,
  )

  useEffect(() => {
    if (item) {
      setAccountId(accounts?.length === 1 ? String(accounts[0].id) : '')
      setSharesText('')
      setCostText('')
      setDate(new Date().toISOString().slice(0, 10))
    }
  }, [item, accounts])

  const buy = useMutation({
    mutationFn: () =>
      api.post<{ washWarning: string | null }>(`/api/watchlist/${item!.id}/bought`, {
        accountId: Number(accountId),
        shares: parseFloat(sharesText),
        totalCost: parseFloat(costText),
        tradeDate: date,
      }),
    onSuccess: (r) => {
      queryClient.invalidateQueries({ queryKey: ['watchlist'] })
      queryClient.invalidateQueries({ queryKey: ['dashboard'] })
      queryClient.invalidateQueries({ queryKey: ['position-detail'] })
      if (r.washWarning) toast.warning(r.washWarning, { duration: 15000 })
      setAskKeep({ id: item!.id, ticker: item!.ticker, shares: parseFloat(sharesText) })
    },
    onError: (e) => toast.error(String(e)),
  })

  const remove = useMutation({
    mutationFn: (id: number) => api.delete(`/api/watchlist/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['watchlist'] }),
    onError: (e) => toast.error(String(e)),
  })

  const done = () => {
    setAskKeep(null)
    onClose()
  }

  const valid =
    accountId !== '' && parseFloat(sharesText) > 0 && parseFloat(costText) >= 0 && date !== ''

  return (
    <>
      <Dialog open={item !== null && askKeep === null} onOpenChange={(o) => !o && onClose()}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle>Buy {item?.ticker}</DialogTitle>
            <DialogDescription>
              Records the lot; the thesis carries into the position's notes.
            </DialogDescription>
          </DialogHeader>
          <div className="grid grid-cols-2 gap-3">
            <div className="col-span-2 space-y-1.5">
              <Label>Account</Label>
              <Select value={accountId} onValueChange={setAccountId}>
                <SelectTrigger>
                  <SelectValue placeholder="Select account" />
                </SelectTrigger>
                <SelectContent>
                  {accounts?.map((a) => (
                    <SelectItem key={a.id} value={String(a.id)}>
                      {a.displayName}
                      {a.isRetirement ? ` (${a.treatmentLabel})` : ''}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="buy-shares">Shares</Label>
              <Input
                id="buy-shares"
                type="number"
                step="any"
                min="0"
                value={sharesText}
                onChange={(e) => setSharesText(e.target.value)}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="buy-cost">Total cost ($)</Label>
              <Input
                id="buy-cost"
                type="number"
                step="0.01"
                min="0"
                value={costText}
                onChange={(e) => setCostText(e.target.value)}
              />
            </div>
            <div className="col-span-2 space-y-1.5">
              <Label htmlFor="buy-date">Trade date</Label>
              <Input
                id="buy-date"
                type="date"
                value={date}
                onChange={(e) => setDate(e.target.value)}
              />
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button onClick={() => buy.mutate()} disabled={!valid || buy.isPending}>
              Buy
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={askKeep !== null} onOpenChange={(o) => !o && done()}>
        <DialogContent className="max-w-sm">
          <DialogHeader>
            <DialogTitle>Position added</DialogTitle>
            <DialogDescription>
              {askKeep &&
                `Bought ${fmtShares(askKeep.shares)} ${askKeep.ticker}. Keep it on the watchlist?`}
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button
              variant="outline"
              onClick={() => {
                if (askKeep) remove.mutate(askKeep.id)
                done()
              }}
            >
              Remove it
            </Button>
            <Button onClick={done}>Keep it</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  )
}
