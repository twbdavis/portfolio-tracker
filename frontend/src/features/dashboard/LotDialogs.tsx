import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { api } from '@/lib/api'
import type { LotDto } from '@/lib/types'
import { useAccounts } from '@/hooks/useAccounts'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
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

/// Manual lot entry (AddPositionForm / OpeningBalanceForm). The wash-sale
/// warning from the API response surfaces as a long-lived toast.
export function AddLotDialog({
  open,
  onOpenChange,
  defaultAccountId,
  fixedTicker,
  openingBalanceHint = false,
}: {
  open: boolean
  onOpenChange: (o: boolean) => void
  defaultAccountId: number | null
  fixedTicker?: string
  openingBalanceHint?: boolean
}) {
  const { data: accounts } = useAccounts()
  const queryClient = useQueryClient()
  const [accountId, setAccountId] = useState<string>('')
  const [ticker, setTicker] = useState(fixedTicker ?? '')
  const [sharesText, setSharesText] = useState('')
  const [costText, setCostText] = useState('')
  const [date, setDate] = useState('')
  const [openingBalance, setOpeningBalance] = useState(openingBalanceHint)

  useEffect(() => {
    if (open) {
      setAccountId(defaultAccountId != null ? String(defaultAccountId) : '')
      setTicker(fixedTicker ?? '')
      setSharesText('')
      setCostText('')
      setDate(new Date().toISOString().slice(0, 10))
      setOpeningBalance(openingBalanceHint)
    }
  }, [open, defaultAccountId, fixedTicker, openingBalanceHint])

  const save = useMutation({
    mutationFn: () =>
      api.post<{ washWarning: string | null }>('/api/lots', {
        accountId: Number(accountId),
        ticker: ticker.trim().toUpperCase(),
        shares: parseFloat(sharesText),
        totalCost: parseFloat(costText),
        tradeDate: date,
        openingBalance,
      }),
    onSuccess: (r) => {
      queryClient.invalidateQueries({ queryKey: ['dashboard'] })
      queryClient.invalidateQueries({ queryKey: ['position-detail'] })
      if (r.washWarning) toast.warning(r.washWarning, { duration: 15000 })
      onOpenChange(false)
    },
    onError: (e) => toast.error(String(e)),
  })

  const valid =
    accountId !== '' &&
    ticker.trim() !== '' &&
    parseFloat(sharesText) > 0 &&
    parseFloat(costText) >= 0 &&
    date !== ''

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>{openingBalance ? 'Opening balance' : 'Add position'}</DialogTitle>
          <DialogDescription>
            {openingBalance
              ? 'Shares you already held before the imported history begins.'
              : 'A manually tracked lot — imports stay authoritative for Chase trades.'}
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
            <Label htmlFor="lot-ticker">Ticker</Label>
            <Input
              id="lot-ticker"
              value={ticker}
              disabled={fixedTicker != null}
              onChange={(e) => setTicker(e.target.value.toUpperCase())}
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="lot-date">Trade date</Label>
            <Input id="lot-date" type="date" value={date} onChange={(e) => setDate(e.target.value)} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="lot-shares">Shares</Label>
            <Input
              id="lot-shares"
              type="number"
              step="any"
              min="0"
              value={sharesText}
              onChange={(e) => setSharesText(e.target.value)}
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="lot-cost">Total cost ($)</Label>
            <Input
              id="lot-cost"
              type="number"
              step="0.01"
              min="0"
              value={costText}
              onChange={(e) => setCostText(e.target.value)}
            />
          </div>
          <div className="col-span-2 flex items-center gap-2 pt-1">
            <Checkbox
              id="lot-opening"
              checked={openingBalance}
              onCheckedChange={(c) => setOpeningBalance(c === true)}
            />
            <Label htmlFor="lot-opening" className="text-sm font-normal">
              Opening balance (held before imported history)
            </Label>
          </div>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button onClick={() => save.mutate()} disabled={!valid || save.isPending}>
            Add
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

export function EditLotDialog({
  lot,
  onClose,
  onSaved,
}: {
  lot: LotDto | null
  onClose: () => void
  onSaved: () => void
}) {
  const [sharesText, setSharesText] = useState('')
  const [costText, setCostText] = useState('')
  const [date, setDate] = useState('')

  useEffect(() => {
    if (lot) {
      setSharesText(String(lot.shares))
      setCostText(String(lot.totalCost))
      setDate(lot.tradeDate)
    }
  }, [lot])

  const save = useMutation({
    mutationFn: () =>
      api.put(`/api/lots/${lot!.sourceTxId}`, {
        shares: parseFloat(sharesText),
        totalCost: parseFloat(costText),
        tradeDate: date,
      }),
    onSuccess: () => {
      onSaved()
      onClose()
    },
    onError: (e) => toast.error(String(e)),
  })

  const valid = parseFloat(sharesText) > 0 && parseFloat(costText) >= 0 && date !== ''

  return (
    <Dialog open={lot !== null} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-sm">
        <DialogHeader>
          <DialogTitle>Edit lot</DialogTitle>
          <DialogDescription>The position rebuilds after saving.</DialogDescription>
        </DialogHeader>
        <div className="grid grid-cols-2 gap-3">
          <div className="space-y-1.5">
            <Label htmlFor="edit-shares">Shares</Label>
            <Input
              id="edit-shares"
              type="number"
              step="any"
              min="0"
              value={sharesText}
              onChange={(e) => setSharesText(e.target.value)}
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="edit-cost">Total cost ($)</Label>
            <Input
              id="edit-cost"
              type="number"
              step="0.01"
              min="0"
              value={costText}
              onChange={(e) => setCostText(e.target.value)}
            />
          </div>
          <div className="col-span-2 space-y-1.5">
            <Label htmlFor="edit-date">Trade date</Label>
            <Input
              id="edit-date"
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
          <Button onClick={() => save.mutate()} disabled={!valid || save.isPending}>
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
