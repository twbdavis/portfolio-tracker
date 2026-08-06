import { describe, expect, it } from 'vitest'
import { applyQuotesToDashboard, applyQuotesToWatchlist } from './quotes'
import type { Dashboard, PositionRow, QuotePush, WatchlistItem } from './types'

function row(overrides: Partial<PositionRow>): PositionRow {
  return {
    positionId: 1,
    accountId: 1,
    ticker: 'MU',
    shares: 10,
    costBasis: 1000,
    avgCost: 100,
    currentPrice: 100,
    priceAsOf: '2026-07-17T10:00:00-04:00',
    marketValue: 1000,
    gainLoss: 0,
    gainLossPct: 0,
    dayGainLoss: 0,
    realizedGainLoss: null,
    targetPrice: null,
    targetDirection: 0,
    alertEnabled: false,
    alertTriggeredAt: null,
    distanceToTargetPct: null,
    needsOpeningBalance: false,
    priceUnavailable: false,
    heldInIra: false,
    iraNote: null,
    ...overrides,
  } as PositionRow
}

function dashboard(rows: PositionRow[]): Dashboard {
  return {
    rows,
    summary: {
      totalMarketValue: rows.reduce((a, r) => a + (r.marketValue ?? 0), 0),
      totalCostBasis: rows.reduce((a, r) => a + r.costBasis, 0),
      totalGainLoss: rows.reduce((a, r) => a + (r.gainLoss ?? 0), 0),
      totalGainLossPct: 0,
      dayGainLoss: 0,
      dayGainLossPct: 0,
      dayStartIsCustom: false,
      unpricedCount: 0,
      deposited: 0,
      cash: 0,
      dividends: 0,
      realized: 0,
      ytdTaxRealized: 0,
      ytdRealized: 0,
      ytdWashAdjustment: 0,
      ytdEstimatedTax: 0,
      accountTotal: 0,
      pricesAsOf: '2026-07-17T10:00:00-04:00',
      quotesObservedAt: null,
      quotesStale: false,
      quotesViaFallback: false,
    },
  }
}

const push = (ticker: string, price: number, prevClose: number | null = null): QuotePush => ({
  ticker,
  price,
  prevClose,
  fetchedAt: '2026-07-17T10:05:00-04:00',
})

describe('applyQuotesToDashboard', () => {
  it('patches price-derived row fields', () => {
    const d = applyQuotesToDashboard(dashboard([row({})]), [push('MU', 110, 105)])
    const r = d.rows[0]
    expect(r.currentPrice).toBe(110)
    expect(r.marketValue).toBe(1100)
    expect(r.gainLoss).toBe(100)
    expect(r.gainLossPct).toBe(10)
    expect(r.dayGainLoss).toBe(50) // 10 shares * (110 - 105)
    expect(r.priceAsOf).toBe('2026-07-17T10:05:00-04:00')
  })

  it('updates summary totals by the market-value delta', () => {
    const d = applyQuotesToDashboard(
      dashboard([row({}), row({ ticker: 'NVDA', shares: 2, costBasis: 400, marketValue: 500, gainLoss: 100 })]),
      [push('MU', 110, 105)],
    )
    expect(d.summary.totalMarketValue).toBe(1600) // 1100 + 500
    expect(d.summary.totalGainLoss).toBe(200) // +100 delta on MU
    expect(d.summary.dayGainLoss).toBe(50)
    expect(d.summary.pricesAsOf).toBe('2026-07-17T10:05:00-04:00')
  })

  it('moves day gain one-for-one with market value under a custom day start', () => {
    const base = dashboard([row({})])
    base.summary.dayStartIsCustom = true
    base.summary.dayGainLoss = 25
    const d = applyQuotesToDashboard(base, [push('MU', 110)])
    expect(d.summary.dayGainLoss).toBe(125) // +100 MV delta, prevClose ignored
  })

  it('computes distance to target like the server', () => {
    const d = applyQuotesToDashboard(dashboard([row({ targetPrice: 121 })]), [push('MU', 110)])
    expect(d.rows[0].distanceToTargetPct).toBeCloseTo(10)
  })

  it('decrements unpriced count when a quote arrives for an unpriced row', () => {
    const base = dashboard([row({ priceUnavailable: true, currentPrice: null, marketValue: null })])
    base.summary.unpricedCount = 1
    const d = applyQuotesToDashboard(base, [push('MU', 110)])
    expect(d.summary.unpricedCount).toBe(0)
    expect(d.rows[0].priceUnavailable).toBe(false)
  })

  it('returns the same object when no ticker matches', () => {
    const base = dashboard([row({})])
    expect(applyQuotesToDashboard(base, [push('NVDA', 1)])).toBe(base)
  })
})

describe('applyQuotesToWatchlist', () => {
  const item: WatchlistItem = {
    id: 1,
    ticker: 'AMD',
    name: null,
    addedAt: '2026-01-01T00:00:00Z',
    thesis: null,
    conviction: 3,
    targetBuyPrice: 90,
    alertEnabled: true,
    alertTriggeredAt: null,
    notes: null,
    currentPrice: 120,
    distanceToTargetPct: -25,
    dayChange: null,
    dayChangePct: null,
  }

  it('patches price and distance', () => {
    const [w] = applyQuotesToWatchlist([item], [push('AMD', 100)])
    expect(w.currentPrice).toBe(100)
    expect(w.distanceToTargetPct).toBeCloseTo(-10)
  })

  it('patches day change from prevClose', () => {
    const [w] = applyQuotesToWatchlist([item], [push('AMD', 105, 100)])
    expect(w.dayChange).toBe(5)
    expect(w.dayChangePct).toBeCloseTo(5)
  })

  it('keeps prior day change when the push lacks prevClose', () => {
    const primed = { ...item, dayChange: 2, dayChangePct: 1.5 }
    const [w] = applyQuotesToWatchlist([primed], [push('AMD', 100)])
    expect(w.dayChange).toBe(2)
    expect(w.dayChangePct).toBe(1.5)
  })

  it('returns the same array when nothing matches', () => {
    const arr = [item]
    expect(applyQuotesToWatchlist(arr, [push('MU', 1)])).toBe(arr)
  })
})
