// Display-only formatting for the money/shares/percent columns. All math
// happened server-side in decimal; nothing here feeds back into calculations.

const usd = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' })
const usdSigned = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  signDisplay: 'always',
})

export function money(value: number | null | undefined): string {
  return value == null ? '—' : usd.format(value)
}

export function signedMoney(value: number | null | undefined): string {
  return value == null ? '—' : usdSigned.format(value)
}

export function signedPercent(value: number | null | undefined, digits = 2): string {
  if (value == null) return '—'
  const sign = value >= 0 ? '+' : ''
  return `${sign}${value.toFixed(digits)}%`
}

export function percent(value: number | null | undefined, digits = 2): string {
  return value == null ? '—' : `${value.toFixed(digits)}%`
}

/// Shares print up to 5 fractional digits, trimming trailing zeros — the
/// WinForms grid's share format.
export function shares(value: number | null | undefined): string {
  if (value == null) return '—'
  return value.toLocaleString('en-US', { maximumFractionDigits: 5 })
}

export function shortDate(iso: string | null | undefined): string {
  if (!iso) return '—'
  const d = new Date(iso + (iso.length === 10 ? 'T00:00:00' : ''))
  return d.toLocaleDateString('en-US', { month: 'numeric', day: 'numeric', year: 'numeric' })
}

export function timeOfDay(iso: string | null | undefined): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })
}

export function gainClass(value: number | null | undefined): string {
  if (value == null) return ''
  return value >= 0 ? 'text-gain' : 'text-loss'
}
