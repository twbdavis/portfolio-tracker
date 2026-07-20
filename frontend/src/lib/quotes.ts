import type { Dashboard, PositionRow, QuotePush, WatchlistItem } from './types'

/// Client-side mirror of PortfolioCalculator's price application, so pushed
/// quotes land on screen instantly without a round-trip. Only price-derived
/// fields are touched; a debounced refetch reconciles the authoritative
/// server numbers shortly after the last wave of a refresh pass.

function patchRow(row: PositionRow, q: QuotePush): PositionRow {
  const marketValue = row.shares * q.price
  const gainLoss = marketValue - row.costBasis
  return {
    ...row,
    currentPrice: q.price,
    priceAsOf: q.fetchedAt,
    priceUnavailable: false,
    marketValue,
    gainLoss,
    gainLossPct: row.costBasis > 0 ? (gainLoss / row.costBasis) * 100 : null,
    dayGainLoss: q.prevClose != null ? row.shares * (q.price - q.prevClose) : row.dayGainLoss,
    distanceToTargetPct:
      row.targetPrice != null && q.price > 0
        ? ((row.targetPrice - q.price) / q.price) * 100
        : row.distanceToTargetPct,
  }
}

export function applyQuotesToDashboard(dash: Dashboard, batch: QuotePush[]): Dashboard {
  const byTicker = new Map(batch.map((q) => [q.ticker.toUpperCase(), q]))
  let mvDelta = 0
  let dayDelta = 0
  let newlyPriced = 0
  let latestFetch = dash.summary.pricesAsOf
  let touched = false

  const rows = dash.rows.map((row) => {
    const q = byTicker.get(row.ticker.toUpperCase())
    if (!q) return row
    touched = true
    if (row.priceUnavailable) newlyPriced++
    const next = patchRow(row, q)
    mvDelta += (next.marketValue ?? 0) - (row.marketValue ?? 0)
    dayDelta += (next.dayGainLoss ?? 0) - (row.dayGainLoss ?? 0)
    if (latestFetch == null || Date.parse(q.fetchedAt) > Date.parse(latestFetch))
      latestFetch = q.fetchedAt
    return next
  })
  if (!touched) return dash

  const s = dash.summary
  const totalMarketValue = s.totalMarketValue + mvDelta
  const totalGainLoss = s.totalGainLoss + mvDelta
  // a custom day-start baseline is a fixed dollar mark, so the day gain moves
  // one-for-one with market value; otherwise sum the per-row day moves
  const dayGainLoss =
    s.dayGainLoss == null ? s.dayGainLoss : s.dayGainLoss + (s.dayStartIsCustom ? mvDelta : dayDelta)
  const dayBase = dayGainLoss != null ? totalMarketValue - dayGainLoss : null
  return {
    rows,
    summary: {
      ...s,
      totalMarketValue,
      totalGainLoss,
      totalGainLossPct: s.totalCostBasis > 0 ? (totalGainLoss / s.totalCostBasis) * 100 : null,
      dayGainLoss,
      dayGainLossPct: dayGainLoss != null && dayBase != null && dayBase > 0 ? (dayGainLoss / dayBase) * 100 : s.dayGainLossPct,
      unpricedCount: Math.max(0, s.unpricedCount - newlyPriced),
      pricesAsOf: latestFetch,
    },
  }
}

export function applyQuotesToWatchlist(items: WatchlistItem[], batch: QuotePush[]): WatchlistItem[] {
  const byTicker = new Map(batch.map((q) => [q.ticker.toUpperCase(), q]))
  let touched = false
  const next = items.map((item) => {
    const q = byTicker.get(item.ticker.toUpperCase())
    if (!q) return item
    touched = true
    return {
      ...item,
      currentPrice: q.price,
      distanceToTargetPct:
        item.targetBuyPrice != null && q.price > 0
          ? ((item.targetBuyPrice - q.price) / q.price) * 100
          : null,
    }
  })
  return touched ? next : items
}
