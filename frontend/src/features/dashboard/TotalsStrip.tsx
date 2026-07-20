import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { cn } from '@/lib/utils'
import { gainClass, money, signedMoney, signedPercent } from '@/lib/format'
import type { DashboardSummary } from '@/lib/types'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { DayStartDialog } from '@/features/dashboard/DayStartDialog'

/// The statement strip under the grid: account cash summary line plus the four
/// big totals, matching the WinForms totals panel value for value.
export function TotalsStrip({
  summary,
  accountId,
}: {
  summary: DashboardSummary
  accountId: number | null
}) {
  const navigate = useNavigate()
  const [dayStartOpen, setDayStartOpen] = useState(false)

  const dayText =
    summary.dayGainLoss == null
      ? '—'
      : `${signedMoney(summary.dayGainLoss)}${
          summary.dayGainLossPct != null ? ` (${signedPercent(summary.dayGainLossPct)})` : ''
        }${summary.dayStartIsCustom ? ' ✎' : ''}`

  const glText = `${signedMoney(summary.totalGainLoss)}${
    summary.totalGainLossPct != null ? ` (${signedPercent(summary.totalGainLossPct)})` : ''
  }`

  return (
    <div className="border-t bg-card px-4 pt-3 pb-4">
      <div className="flex flex-wrap items-baseline gap-x-6 gap-y-1 pb-3 text-sm">
        <Metric label="Deposited" value={money(summary.deposited)} />
        <Metric label="Cash (sweep)" value={money(summary.cash)} />
        <Metric label="Dividends" value={money(summary.dividends)} />
        <Metric
          label="Realized Gain/Loss"
          value={signedMoney(summary.realized)}
          valueClass={gainClass(summary.realized)}
        />
        <Tooltip>
          <TooltipTrigger asChild>
            <button className="flex items-baseline gap-1.5" onClick={() => navigate('/taxes')}>
              <span className="text-xs text-muted-foreground">YTD Realized (after tax)</span>
              <span className={cn('font-semibold tnum', gainClass(summary.ytdTaxRealized))}>
                {signedMoney(summary.ytdTaxRealized)}
              </span>
            </button>
          </TooltipTrigger>
          <TooltipContent>
            This year's wash-adjusted realized gain/loss minus estimated federal tax (matches
            Realized After Tax on the Taxes page) — click for the tax dashboard
          </TooltipContent>
        </Tooltip>
        <Metric
          label={accountId == null ? 'Portfolio Total' : 'Account Total'}
          value={money(summary.accountTotal)}
        />
      </div>
      <div className="grid grid-cols-4 gap-4 border-t pt-3">
        <BigMetric label="Market Value" value={money(summary.totalMarketValue)} />
        <BigMetric label="Cost Basis" value={money(summary.totalCostBasis)} />
        {accountId == null ? (
          <Tooltip>
            <TooltipTrigger asChild>
              <button className="text-left" onClick={() => setDayStartOpen(true)}>
                <BigMetric
                  label="Daily Gain/Loss"
                  value={dayText}
                  valueClass={summary.dayGainLoss != null ? gainClass(summary.dayGainLoss) : ''}
                />
              </button>
            </TooltipTrigger>
            <TooltipContent>Click to set today's starting value</TooltipContent>
          </Tooltip>
        ) : (
          <BigMetric
            label="Daily Gain/Loss"
            value={dayText}
            valueClass={summary.dayGainLoss != null ? gainClass(summary.dayGainLoss) : ''}
          />
        )}
        <BigMetric
          label="Unrealized Gain/Loss"
          value={glText}
          valueClass={gainClass(summary.totalGainLoss)}
        />
      </div>

      <DayStartDialog
        open={dayStartOpen}
        onOpenChange={setDayStartOpen}
        marketValue={summary.totalMarketValue}
        prevCloseBaseline={
          summary.dayGainLoss != null && !summary.dayStartIsCustom
            ? summary.totalMarketValue - summary.dayGainLoss
            : null
        }
        isCustom={summary.dayStartIsCustom}
      />
    </div>
  )
}

function Metric({
  label,
  value,
  valueClass,
}: {
  label: string
  value: string
  valueClass?: string
}) {
  return (
    <div className="flex items-baseline gap-1.5">
      <span className="text-xs text-muted-foreground">{label}</span>
      <span className={cn('font-semibold tnum', valueClass)}>{value}</span>
    </div>
  )
}

function BigMetric({
  label,
  value,
  valueClass,
}: {
  label: string
  value: string
  valueClass?: string
}) {
  return (
    <div>
      <div className="text-[11px] font-medium tracking-wider text-muted-foreground uppercase">
        {label}
      </div>
      <div className={cn('text-lg font-semibold tnum', valueClass)}>{value}</div>
    </div>
  )
}
