import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Download, FlaskConical } from 'lucide-react'
import { toast } from 'sonner'
import { api, downloadFile } from '@/lib/api'
import { cn } from '@/lib/utils'
import { gainClass, money, shares as fmtShares, shortDate, signedMoney } from '@/lib/format'
import type { TaxEvent, TaxYearData, WashRisk } from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
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
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { SellSimulatorDialog } from '@/features/simulator/SellSimulatorDialog'

/// The TaxesForm: wash-adjusted realized results, transparent netting with
/// carryforward, dividend income, the estimated federal tax attributable to
/// investments, and the Form 8949-style export. Estimates only.
export function TaxesPage() {
  const currentYear = new Date().getFullYear()
  const [year, setYear] = useState(currentYear)
  const [simOpen, setSimOpen] = useState(false)
  const [carryOpen, setCarryOpen] = useState(false)

  const query = useQuery({
    queryKey: ['tax', year],
    queryFn: () => api.get<TaxYearData>(`/api/tax/${year}`),
  })
  const washQuery = useQuery({
    queryKey: ['washrisks'],
    queryFn: () => api.get<WashRisk[]>('/api/tax/washrisks'),
  })

  const d = query.data
  const years = d?.availableYears ?? [currentYear]

  if (query.isLoading)
    return (
      <div className="space-y-3 p-4">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-64 w-full" />
      </div>
    )

  return (
    <div className="space-y-4 p-4">
      <div className="flex flex-wrap items-center gap-2">
        <Label className="text-muted-foreground">Tax year</Label>
        <Select value={String(year)} onValueChange={(v) => setYear(Number(v))}>
          <SelectTrigger className="w-28">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {years.map((y) => (
              <SelectItem key={y} value={String(y)}>
                {y}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <div className="flex-1" />
        <Button variant="outline" onClick={() => setSimOpen(true)}>
          <FlaskConical className="size-4" /> What if I sell…
        </Button>
        <Button
          variant="outline"
          disabled={!d}
          onClick={() =>
            downloadFile(`/api/tax/${year}/8949.csv`, `form8949-${year}.csv`)
              .then(() => toast.success(`Exported ${d?.events.length ?? 0} events`))
              .catch((e) => toast.error(String(e)))
          }
        >
          <Download className="size-4" /> Export 8949 CSV
        </Button>
      </div>

      {d && (
        <>
          <SummaryCards d={d} />
          <NettingPanel d={d} onEditCarry={() => setCarryOpen(true)} />
          <WashWindows risks={washQuery.data ?? []} />

          <Tabs defaultValue="events">
            <TabsList>
              <TabsTrigger value="events">Realized Events</TabsTrigger>
              <TabsTrigger value="dividends">Dividends</TabsTrigger>
            </TabsList>
            <TabsContent value="events">
              <EventsTable events={d.events} />
            </TabsContent>
            <TabsContent value="dividends">
              <DividendsTable d={d} />
            </TabsContent>
          </Tabs>
        </>
      )}

      <p className="border-t pt-3 text-xs text-warn">
        Estimates only — your broker's 1099 and a tax professional are authoritative.
      </p>

      <SellSimulatorDialog
        row={null}
        accountId={null}
        open={simOpen}
        onClose={() => setSimOpen(false)}
      />
      {d && <CarryforwardDialog open={carryOpen} onOpenChange={setCarryOpen} d={d} />}
    </div>
  )
}

function SummaryCards({ d }: { d: TaxYearData }) {
  // economic money kept: raw realized − estimated tax (the tax itself is
  // figured on the wash-adjusted amount; the add-back is a lost deduction,
  // not money, so it never lands in this headline)
  const afterTax = d.yearRealized - d.estimate.attributable
  const cards: { caption: string; value: string; cls: string; show?: boolean }[] = [
    {
      caption: 'Net Short-Term',
      value: signedMoney(d.netting.netShortTerm),
      cls: gainClass(d.netting.netShortTerm),
    },
    {
      caption: 'Net Long-Term',
      value: signedMoney(d.netting.netLongTerm),
      cls: gainClass(d.netting.netLongTerm),
    },
    {
      caption: 'Wash Losses Disallowed',
      value: money(d.totalDisallowed),
      cls: d.totalDisallowed > 0 ? 'text-warn' : '',
    },
    { caption: 'Gross Dividends', value: money(d.grossDividends), cls: '' },
    {
      caption: 'Est. Federal Tax (Investments)',
      value: signedMoney(d.estimate.attributable),
      cls: d.estimate.attributable > 0 ? 'text-loss' : 'text-gain',
    },
    { caption: 'Realized After Tax', value: signedMoney(afterTax), cls: gainClass(afterTax) },
    {
      caption: 'Permanently Lost (IRA Wash)',
      value: money(d.permanentlyLost),
      cls: 'text-loss',
      show: d.permanentlyLost > 0,
    },
  ]
  return (
    <div className="grid grid-cols-2 gap-2 md:grid-cols-3 xl:grid-cols-6">
      {cards
        .filter((c) => c.show !== false)
        .map((c) => (
          <Card key={c.caption}>
            <CardContent className="px-3 py-2">
              <div className="text-[10px] font-medium uppercase tracking-wide text-muted-foreground">
                {c.caption}
              </div>
              <div className={cn('tnum text-lg font-semibold', c.cls)}>{c.value}</div>
            </CardContent>
          </Card>
        ))}
    </div>
  )
}

/// The netting breakdown, line for line the WinForms panel.
function NettingPanel({ d, onEditCarry }: { d: TaxYearData; onEditCarry: () => void }) {
  const carrySrc = d.carryInIsManual ? 'manual' : 'computed'
  const hasCarryIn = d.carryInShortTerm > 0 || d.carryInLongTerm > 0
  return (
    <Card>
      <CardContent className="space-y-1 px-3 py-2 text-sm">
        <div className="tnum">
          Year results:&nbsp; short-term {signedMoney(d.yearShortTerm)},&nbsp; long-term{' '}
          {signedMoney(d.yearLongTerm)}
          {hasCarryIn && (
            <>
              &nbsp;&nbsp;|&nbsp;&nbsp;carryforward applied ({carrySrc}): ST −
              {money(d.carryInShortTerm)}, LT −{money(d.carryInLongTerm)}
              {d.carryInIsManual && (
                <Badge variant="outline" className="ml-1.5 border-warn/60 text-warn">
                  manual
                </Badge>
              )}
            </>
          )}
        </div>
        <div className="tnum">
          After netting:&nbsp; taxable short-term {money(d.netting.taxableShortTerm)},&nbsp;
          taxable long-term {money(d.netting.taxableLongTerm)}
        </div>
        {d.netting.ordinaryOffset > 0 && (
          <div className="tnum">
            Net loss offsets {money(d.netting.ordinaryOffset)} of ordinary income (cap $3,000)
          </div>
        )}
        {(d.netting.carryforwardShortTerm > 0 || d.netting.carryforwardLongTerm > 0) && (
          <div className="tnum">
            Carryforward to {d.year + 1}:&nbsp; ST {money(d.netting.carryforwardShortTerm)},&nbsp;
            LT {money(d.netting.carryforwardLongTerm)}
          </div>
        )}
        {d.retirementAccountsExcluded > 0 && (
          <div className="text-muted-foreground">
            Excludes {d.retirementAccountsExcluded} retirement account(s) — IRA sales and dividends
            are not taxable events
          </div>
        )}
        {d.permanentlyLost > 0 && (
          <div className="tnum text-loss">
            ⚠ IRA wash — losses permanently lost this year: {money(d.permanentlyLost)} (Rev. Rul.
            2008-5; no basis adjustment anywhere)
          </div>
        )}
        {d.bracketsAreFallback && (
          <div className="text-warn">
            ⚠ taxbrackets.json has no {d.year} entry — using the nearest year's brackets
          </div>
        )}
        <button
          className="text-xs text-primary underline-offset-2 hover:underline"
          onClick={onEditCarry}
        >
          Edit carryforward…
        </button>
      </CardContent>
    </Card>
  )
}

/// Open wash windows: tickers not to rebuy right now.
function WashWindows({ risks }: { risks: WashRisk[] }) {
  return (
    <Card>
      <CardContent className="px-3 py-2">
        <div className="text-[10px] font-medium uppercase tracking-wide text-muted-foreground">
          Wash-Sale Windows Open
        </div>
        {risks.length === 0 ? (
          <div className="text-sm text-muted-foreground">
            None — no losses realized in the last 30 days. Buying anything is wash-safe.
          </div>
        ) : (
          <div className="mt-1 space-y-1 text-sm text-warn">
            {risks.map((r) => (
              <div key={r.ticker} className="tnum">
                {r.ticker} — buying before {shortDate(r.safeToBuyOn)} disallows{' '}
                {money(r.lossAtRisk)} of the loss sold {shortDate(r.latestSale)} (
                {r.daysToWait} day{r.daysToWait === 1 ? '' : 's'} to wait)
              </div>
            ))}
          </div>
        )}
      </CardContent>
    </Card>
  )
}

/// Distinguish the wash flavors: same-account defers, cross-account defers but
/// appears on no 1099-B, IRA destroys the deduction permanently.
function WashBadges({ e }: { e: TaxEvent }) {
  if (!e.isWash) return null
  const sameAccount = e.disallowed - e.crossAccountDisallowed - e.iraDisallowed
  return (
    <div className="flex flex-wrap gap-1">
      {sameAccount > 0 && (
        <Badge variant="outline" className="border-warn/60 text-warn">
          wash −{money(sameAccount)}
        </Badge>
      )}
      {e.hasCrossAccountWash && (
        <Badge
          variant="outline"
          className="border-warn/60 text-warn"
          title="Cross-account wash — will NOT appear on either 1099-B"
        >
          wash −{money(e.crossAccountDisallowed)} cross-account (on no 1099-B)
        </Badge>
      )}
      {e.hasIraWash && (
        <Badge variant="outline" className="border-loss/60 text-loss">
          wash — permanent (IRA) −{money(e.iraDisallowed)}
        </Badge>
      )}
    </div>
  )
}

function EventsTable({ events }: { events: TaxEvent[] }) {
  const [tickerFilter, setTickerFilter] = useState('')
  const [termFilter, setTermFilter] = useState('all')

  const rows = useMemo(
    () =>
      events
        .filter(
          (e) =>
            tickerFilter.trim() === '' ||
            e.ticker.toLowerCase().includes(tickerFilter.trim().toLowerCase()),
        )
        .filter((e) =>
          termFilter === 'short'
            ? e.term === 'ShortTerm'
            : termFilter === 'long'
              ? e.term === 'LongTerm'
              : true,
        ),
    [events, tickerFilter, termFilter],
  )

  const totals = useMemo(
    () => ({
      proceeds: rows.reduce((s, r) => s + r.proceeds, 0),
      basis: rows.reduce((s, r) => s + r.basis, 0),
      gainLoss: rows.reduce((s, r) => s + r.gainLoss, 0),
      disallowed: rows.reduce((s, r) => s + r.disallowed, 0),
      iraDisallowed: rows.reduce((s, r) => s + r.iraDisallowed, 0),
    }),
    [rows],
  )

  return (
    <div className="space-y-2 pt-2">
      <div className="flex gap-2">
        <Input
          placeholder="Filter ticker"
          className="w-36"
          value={tickerFilter}
          onChange={(e) => setTickerFilter(e.target.value)}
        />
        <Select value={termFilter} onValueChange={setTermFilter}>
          <SelectTrigger className="w-36">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All terms</SelectItem>
            <SelectItem value="short">Short-term</SelectItem>
            <SelectItem value="long">Long-term</SelectItem>
          </SelectContent>
        </Select>
      </div>

      <table className="w-full text-sm">
        <thead>
          <tr className="border-b text-xs uppercase text-muted-foreground">
            <th className="px-2 py-1.5 text-left font-medium">Ticker</th>
            <th className="px-2 py-1.5 text-left font-medium">Account</th>
            <th className="px-2 py-1.5 text-left font-medium">Acquired</th>
            <th className="px-2 py-1.5 text-left font-medium">Sold</th>
            <th className="px-2 py-1.5 text-right font-medium">Days</th>
            <th className="px-2 py-1.5 text-left font-medium">Term</th>
            <th className="px-2 py-1.5 text-right font-medium">Shares</th>
            <th className="px-2 py-1.5 text-right font-medium">Proceeds</th>
            <th className="px-2 py-1.5 text-right font-medium">Basis</th>
            <th className="px-2 py-1.5 text-right font-medium">Gain/Loss</th>
            <th className="px-2 py-1.5 text-left font-medium">Wash</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((e, i) => (
            <tr key={i} className="border-b border-border/50">
              <td className="px-2 py-1.5 font-medium">{e.ticker}</td>
              <td className="px-2 py-1.5">{e.account}</td>
              <td className="px-2 py-1.5">{shortDate(e.acquired)}</td>
              <td className="px-2 py-1.5">{shortDate(e.sold)}</td>
              <td className="tnum px-2 py-1.5 text-right">{e.daysHeld}</td>
              <td className="px-2 py-1.5">{e.term === 'ShortTerm' ? 'Short' : 'Long'}</td>
              <td className="tnum px-2 py-1.5 text-right">{fmtShares(e.shares)}</td>
              <td className="tnum px-2 py-1.5 text-right">{money(e.proceeds)}</td>
              <td className="tnum px-2 py-1.5 text-right">{money(e.basis)}</td>
              <td className={cn('tnum px-2 py-1.5 text-right', gainClass(e.gainLoss))}>
                {signedMoney(e.gainLoss)}
              </td>
              <td className="px-2 py-1.5">
                <WashBadges e={e} />
              </td>
            </tr>
          ))}
          {rows.length === 0 && (
            <tr>
              <td colSpan={11} className="px-2 py-6 text-center text-muted-foreground">
                No realized events this year.
              </td>
            </tr>
          )}
        </tbody>
      </table>

      <div className="tnum rounded-md bg-card px-3 py-2 text-sm font-medium">
        {rows.length} events&nbsp;&nbsp;|&nbsp;&nbsp;Proceeds {money(totals.proceeds)}
        &nbsp;&nbsp;|&nbsp;&nbsp;Basis {money(totals.basis)}&nbsp;&nbsp;|&nbsp;&nbsp;Gain/Loss{' '}
        <span className={gainClass(totals.gainLoss)}>{signedMoney(totals.gainLoss)}</span>
        &nbsp;&nbsp;|&nbsp;&nbsp;Disallowed {money(totals.disallowed)}
        {totals.iraDisallowed > 0 && (
          <>
            &nbsp;&nbsp;|&nbsp;&nbsp;
            <span className="text-loss">Permanently lost (IRA) {money(totals.iraDisallowed)}</span>
          </>
        )}
      </div>
    </div>
  )
}

function DividendsTable({ d }: { d: TaxYearData }) {
  return (
    <div className="space-y-2 pt-2">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b text-xs uppercase text-muted-foreground">
            <th className="px-2 py-1.5 text-left font-medium">Ticker</th>
            <th className="px-2 py-1.5 text-right font-medium">
              Gross Dividends (see 1099-DIV for qualified split)
            </th>
          </tr>
        </thead>
        <tbody>
          {d.dividendsByTicker.map((x) => (
            <tr key={x.ticker} className="border-b border-border/50">
              <td className="px-2 py-1.5 font-medium">{x.ticker}</td>
              <td className="tnum px-2 py-1.5 text-right">{money(x.amount)}</td>
            </tr>
          ))}
          {d.dividendsByTicker.length === 0 && (
            <tr>
              <td colSpan={2} className="px-2 py-6 text-center text-muted-foreground">
                No taxable dividends this year.
              </td>
            </tr>
          )}
        </tbody>
      </table>
      {d.dividendsByTicker.length > 0 && (
        <div className="tnum rounded-md bg-card px-3 py-2 text-sm font-medium">
          Total {money(d.grossDividends)}
        </div>
      )}
    </div>
  )
}

/// Edit the loss carryforward coming INTO the selected year — the prior year's
/// row, in case the real return differs from the computed value.
function CarryforwardDialog({
  open,
  onOpenChange,
  d,
}: {
  open: boolean
  onOpenChange: (o: boolean) => void
  d: TaxYearData
}) {
  const priorYear = d.year - 1
  const queryClient = useQueryClient()
  const [st, setSt] = useState('')
  const [lt, setLt] = useState('')

  // prefill from the applied carry-in whenever the dialog opens
  useEffect(() => {
    if (open) {
      setSt(String(d.carryInShortTerm))
      setLt(String(d.carryInLongTerm))
    }
  }, [open, d.carryInShortTerm, d.carryInLongTerm])

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['tax'] })

  const save = useMutation({
    mutationFn: () =>
      api.patch(`/api/tax/${priorYear}/carryforward`, {
        shortTerm: parseFloat(st),
        longTerm: parseFloat(lt),
      }),
    onSuccess: () => {
      invalidate()
      toast.success('Manual carryforward saved')
      onOpenChange(false)
    },
    onError: (e) => toast.error(String(e)),
  })

  const reset = useMutation({
    mutationFn: () => api.delete(`/api/tax/${priorYear}/carryforward`),
    onSuccess: () => {
      invalidate()
      toast.success('Reset to the computed carryforward')
      onOpenChange(false)
    },
    onError: (e) => toast.error(String(e)),
  })

  const valid = parseFloat(st) >= 0 && parseFloat(lt) >= 0

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Carryforward out of {priorYear}</DialogTitle>
          <DialogDescription>
            {d.carryInIsManual
              ? 'Currently a manual value. Reset returns to the computed amount.'
              : 'Enter the amounts from your actual return (positive numbers).'}
          </DialogDescription>
        </DialogHeader>
        <div className="grid grid-cols-2 gap-3">
          <div className="space-y-1.5">
            <Label htmlFor="carry-st">Short-term loss carried</Label>
            <Input
              id="carry-st"
              inputMode="decimal"
              value={st}
              onChange={(e) => setSt(e.target.value)}
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="carry-lt">Long-term loss carried</Label>
            <Input
              id="carry-lt"
              inputMode="decimal"
              value={lt}
              onChange={(e) => setLt(e.target.value)}
            />
          </div>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button
            variant="outline"
            disabled={!d.carryInIsManual || reset.isPending}
            onClick={() => reset.mutate()}
          >
            Reset to Computed
          </Button>
          <Button disabled={!valid || save.isPending} onClick={() => save.mutate()}>
            Save Manual Value
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
