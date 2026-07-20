import { describe, expect, it } from 'vitest'
import { gainClass, money, percent, shares, shortDate, signedMoney, signedPercent } from './format'

describe('money', () => {
  it('formats USD', () => expect(money(1234.5)).toBe('$1,234.50'))
  it('em-dashes null', () => expect(money(null)).toBe('—'))
  it('parenthesizes nothing — negatives keep the minus sign', () =>
    expect(money(-42)).toBe('-$42.00'))
})

describe('signedMoney', () => {
  it('adds a plus for gains', () => expect(signedMoney(42)).toBe('+$42.00'))
  it('keeps the minus for losses', () => expect(signedMoney(-42)).toBe('-$42.00'))
  it('em-dashes null', () => expect(signedMoney(null)).toBe('—'))
})

describe('percent formatting', () => {
  it('signedPercent adds a plus', () => expect(signedPercent(3.456)).toBe('+3.46%'))
  it('signedPercent keeps minus', () => expect(signedPercent(-3.456)).toBe('-3.46%'))
  it('percent is unsigned', () => expect(percent(3.456)).toBe('3.46%'))
  it('null is an em-dash', () => expect(signedPercent(null)).toBe('—'))
})

describe('shares', () => {
  it('trims trailing zeros up to 5 places', () => expect(shares(0.05125)).toBe('0.05125'))
  it('keeps whole numbers whole', () => expect(shares(12)).toBe('12'))
  it('groups thousands', () => expect(shares(1234.5)).toBe('1,234.5'))
})

describe('shortDate', () => {
  it('renders date-only ISO strings without timezone drift', () =>
    expect(shortDate('2026-07-10')).toBe('7/10/2026'))
  it('em-dashes null', () => expect(shortDate(null)).toBe('—'))
})

describe('gainClass', () => {
  it('zero counts as gain (WinForms >= 0 rule)', () => expect(gainClass(0)).toBe('text-gain'))
  it('negative is loss', () => expect(gainClass(-0.01)).toBe('text-loss'))
  it('null is unstyled', () => expect(gainClass(null)).toBe(''))
})
