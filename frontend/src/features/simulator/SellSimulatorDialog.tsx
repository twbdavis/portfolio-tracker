import { useEffect, useMemo, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Clock } from 'lucide-react'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'
import { gainClass, money, shares as fmtShares, shortDate, signedMoney } from '@/lib/format'
import type { Dashboard, PositionDetail, PositionRow, SellSimulation } from '@/lib/types'
import { useAccounts } from '@/hooks/useAccounts'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
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

/// "What if I sell?" — the SellSimulatorForm as a dialog. Opened from a
/// position row (ticker fixed, accounts limited to those holding it) or from
/// /taxes via `open` (account + ticker pickers, like the WinForms button).
/// Simulation only: nothing is recorded.
export function SellSimulatorDialog({
  row,
  accountId,
  onClose,
  open,
}: {
  row: PositionRow | null
  accountId: number | null
  onClose: () => void
  open?: boolean
}) {
  const isOpen = open ?? row !== null
  return (
    <Dialog open={isOpen} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="flex max-h-[85vh] flex-col gap-4 overflow-y-auto sm:max-w-3xl">
        {isOpen && <SimulatorBody row={row} accountId={accountId} />}
      </DialogContent>
    </Dialog>
  )
}

function SimulatorBody({ row, accountId }: { row: PositionRow | null; accountId: number | null }) {
  const { data: allAccounts } = useAccounts()
  const [acctId, setAcctId] = useState<string>('')
  const [ticker, setTicker] = useState<string>(row?.ticker ?? '')
  const [sharesText, setSharesText] = useState('')
  const [priceText, setPriceText] = useState('')

  // row mode: the cumulative row may span accounts — list the holders
  const detail = useQuery({
    queryKey: ['position-detail', row?.ticker],
    queryFn: () => api.get<PositionDetail>(`/api/positions/${encodeURIComponent(row!.ticker)}/detail`),
    enabled: row !== null,
  })

  // manual mode: open positions within the selected account
  const accountRows = useQuery({
    queryKey: ['dashboard', Number(acctId)],
    queryFn: () => api.get<Dashboard>(`/api/dashboard/account/${acctId}`),
    enabled: row === null && acctId !== '',
  })

  const holdingAccounts = useMemo(() => {
    if (row === null) return null
    return (detail.data?.accounts ?? []).filter((a) => a.lots.some((l) => l.remainingShares > 0))
  }, [row, detail.data])

  const openSharesIn = (id: string): number | null => {
    if (row !== null) {
      const acct = holdingAccounts?.find((a) => String(a.accountId) === id)
      return acct ? acct.lots.reduce((s, l) => s + l.remainingShares, 0) : null
    }
    const r = accountRows.data?.rows.find((x) => x.ticker === ticker)
    return r ? r.shares : null
  }

  // FIFO preview is per account: default to the launching row's account
  useEffect(() => {
    if (row !== null && holdingAccounts && acctId === '') {
      const preferred =
        holdingAccounts.find((a) => a.accountId === accountId) ??
        holdingAccounts.find((a) => a.accountId === row.accountId) ??
        (holdingAccounts.length >= 1 ? holdingAccounts[0] : undefined)
      if (preferred) setAcctId(String(preferred.accountId))
    }
    if (row === null && allAccounts && allAccounts.length > 0 && acctId === '') {
      const preferred =
        allAccounts.find((a) => a.id === accountId) ??
        (allAccounts.length === 1 ? allAccounts[0] : undefined)
      if (preferred) setAcctId(String(preferred.id))
    }
  }, [row, holdingAccounts, allAccounts, accountId, acctId])

  // prefill shares (full position in the account) and price (last quote)
  useEffect(() => {
    if (acctId === '' || ticker === '') return
    const open = openSharesIn(acctId)
    if (open != null) setSharesText(String(open))
    const price =
      row?.currentPrice ?? accountRows.data?.rows.find((x) => x.ticker === ticker)?.currentPrice
    if (price != null) setPriceText(String(price))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [acctId, ticker, holdingAccounts, accountRows.data])

  const shares = parseFloat(sharesText)
  const price = parseFloat(priceText)
  const valid = acctId !== '' && ticker !== '' && shares > 0 && price > 0

  // live re-simulate, debounced; stale responses are dropped
  const [sim, setSim] = useState<SellSimulation | null>(null)
  const [simError, setSimError] = useState<string | null>(null)
  const runId = useRef(0)
  useEffect(() => {
    if (!valid) {
      setSim(null)
      setSimError(null)
      return
    }
    const id = ++runId.current
    const t = setTimeout(async () => {
      try {
        const result = await api.post<SellSimulation>('/api/simulator/sell', {
          accountId: Number(acctId),
          ticker,
          shares,
          price,
        })
        if (runId.current === id) {
          setSim(result)
          setSimError(null)
        }
      } catch (e) {
        if (runId.current === id) {
          setSim(null)
          setSimError(String(e))
        }
      }
    }, 350)
    return () => clearTimeout(t)
  }, [valid, acctId, ticker, shares, price])

  const accountName =
    row !== null
      ? holdingAccounts?.find((a) => String(a.accountId) === acctId)?.account
      : allAccounts?.find((a) => String(a.id) === acctId)?.displayName
  const tickerOptions =
    row === null ? (accountRows.data?.rows ?? []).filter((r) => r.shares > 0) : null

  return (
    <>
      <DialogHeader>
        <DialogTitle>What if I sell{ticker ? ` ${ticker}` : ''}?</DialogTitle>
        <DialogDescription>
          FIFO consumption preview with wash-adjusted basis — per account. Nothing is recorded.
        </DialogDescription>
      </DialogHeader>

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <div className="space-y-1.5">
          <Label>Account</Label>
          <Select
            value={acctId}
            onValueChange={(v) => {
              setAcctId(v)
              if (row === null) setTicker('')
            }}
          >
            <SelectTrigger>
              <SelectValue placeholder="Account" />
            </SelectTrigger>
            <SelectContent>
              {row !== null
                ? holdingAccounts?.map((a) => (
                    <SelectItem key={a.accountId} value={String(a.accountId)}>
                      {a.account}
                      {a.isRetirement ? ` (${a.treatmentLabel})` : ''}
                    </SelectItem>
                  ))
                : allAccounts?.map((a) => (
                    <SelectItem key={a.id} value={String(a.id)}>
                      {a.displayName}
                      {a.isRetirement ? ` (${a.treatmentLabel})` : ''}
                    </SelectItem>
                  ))}
            </SelectContent>
          </Select>
        </div>
        <div className="space-y-1.5">
          <Label>Ticker</Label>
          {row !== null ? (
            <Input value={ticker} disabled />
          ) : (
            <Select value={ticker} onValueChange={setTicker}>
              <SelectTrigger>
                <SelectValue placeholder="Ticker" />
              </SelectTrigger>
              <SelectContent>
                {tickerOptions?.map((r) => (
                  <SelectItem key={r.ticker} value={r.ticker}>
                    {r.ticker}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          )}
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="sim-shares">Shares</Label>
          <div className="flex gap-1.5">
            <Input
              id="sim-shares"
              inputMode="decimal"
              value={sharesText}
              onChange={(e) => setSharesText(e.target.value)}
            />
            <Button
              variant="outline"
              className="shrink-0"
              onClick={() => {
                const open = openSharesIn(acctId)
                if (open != null) setSharesText(String(open))
              }}
            >
              All
            </Button>
          </div>
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="sim-price">Price</Label>
          <Input
            id="sim-price"
            inputMode="decimal"
            value={priceText}
            onChange={(e) => setPriceText(e.target.value)}
          />
        </div>
      </div>

      {simError && (
        <Alert variant="destructive">
          <AlertDescription>{simError}</AlertDescription>
        </Alert>
      )}

      {sim && (
        <div className="space-y-3">
          <div className="rounded-md border bg-card px-3 py-2 text-sm">
            <div className="tnum">
              Proceeds <span className="font-semibold">{money(sim.totalProceeds)}</span>
              {sim.sharesRequested > sim.sharesAvailable && (
                <span className="text-muted-foreground">
                  {' '}
                  (only {fmtShares(sim.sharesAvailable)} shares held in{' '}
                  {accountName ?? 'this account'})
                </span>
              )}
              <span className="text-muted-foreground"> | </span>
              Short-term{' '}
              <span className={cn('font-semibold', gainClass(sim.shortTermGainLoss))}>
                {signedMoney(sim.shortTermGainLoss)}
              </span>
              <span className="text-muted-foreground"> | </span>
              Long-term{' '}
              <span className={cn('font-semibold', gainClass(sim.longTermGainLoss))}>
                {signedMoney(sim.longTermGainLoss)}
              </span>
            </div>
            <div className="mt-1 text-muted-foreground">
              Estimated federal tax on this sale:{' '}
              <span
                className={cn(
                  'tnum font-semibold',
                  sim.estimatedTaxOnSale > 0 ? 'text-loss' : 'text-gain',
                )}
              >
                {signedMoney(sim.estimatedTaxOnSale)}
              </span>{' '}
              (stacked on YTD realized results and your settings)
            </div>
          </div>

          {sim.washWarning && (
            <Alert className="border-warn/50 text-warn">
              <AlertTitle>Wash sale</AlertTitle>
              <AlertDescription className="text-warn/90">{sim.washWarning}</AlertDescription>
            </Alert>
          )}
          {sim.iraWashWarning && (
            <Alert variant="destructive">
              <AlertTitle>IRA wash — loss permanently lost</AlertTitle>
              <AlertDescription>{sim.iraWashWarning}</AlertDescription>
            </Alert>
          )}

          {sim.longTermCallouts.length > 0 && (
            <div className="space-y-1 rounded-md border-2 border-gain/60 bg-gain/5 px-3 py-2">
              {sim.longTermCallouts.map((c, i) => (
                <div key={i} className="flex items-center gap-2 text-sm font-medium text-gain">
                  <Clock className="size-4 shrink-0" />
                  <span>
                    Lot of {fmtShares(c.shares)} shares goes long-term in {c.days} days (
                    {shortDate(c.date)})
                    {c.estimatedSavings > 0 && <> — waiting saves ~{money(c.estimatedSavings)}</>}
                  </span>
                </div>
              ))}
            </div>
          )}

          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-xs uppercase text-muted-foreground">
                <th className="px-2 py-1.5 text-left font-medium">Lot Acquired</th>
                <th className="px-2 py-1.5 text-right font-medium">Shares</th>
                <th className="px-2 py-1.5 text-left font-medium">Term</th>
                <th className="px-2 py-1.5 text-right font-medium">Basis</th>
                <th className="px-2 py-1.5 text-right font-medium">Proceeds</th>
                <th className="px-2 py-1.5 text-right font-medium">Gain/Loss</th>
                <th className="px-2 py-1.5 text-left font-medium">Long-Term On</th>
              </tr>
            </thead>
            <tbody>
              {sim.portions.map((p, i) => (
                <tr key={i} className="border-b border-border/50">
                  <td className="px-2 py-1.5">{shortDate(p.acquired)}</td>
                  <td className="tnum px-2 py-1.5 text-right">{fmtShares(p.shares)}</td>
                  <td className="px-2 py-1.5">{p.term === 'ShortTerm' ? 'Short' : 'Long'}</td>
                  <td className="tnum px-2 py-1.5 text-right">{money(p.basis)}</td>
                  <td className="tnum px-2 py-1.5 text-right">{money(p.proceeds)}</td>
                  <td className={cn('tnum px-2 py-1.5 text-right', gainClass(p.gainLoss))}>
                    {signedMoney(p.gainLoss)}
                  </td>
                  <td className="px-2 py-1.5">
                    {p.term === 'ShortTerm' ? shortDate(p.longTermOn) : ''}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <p className="text-xs text-warn">
        Simulation only — nothing is recorded. Estimates only; your broker's 1099 and a tax
        professional are authoritative.
      </p>
    </>
  )
}
