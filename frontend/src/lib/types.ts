// DTO mirrors of the API. Money decimals arrive as JSON numbers; formatting
// (and any rounding) is display-only in lib/format.ts.

export type TaxTreatment = 'Taxable' | 'RothIRA' | 'TraditionalIRA'
export type TargetDirection = 'AtOrAbove' | 'AtOrBelow'
export type LotSource = 'Manual' | 'ChaseImport' | 'OpeningBalance'

export interface Account {
  id: number
  accountNumber: string
  displayName: string
  accountNameRaw: string | null
  taxTreatment: TaxTreatment
  treatmentLabel: string
  isRetirement: boolean
  sortOrder: number
  needsReview: boolean
}

export interface PositionRow {
  positionId: number
  accountId: number
  ticker: string
  shares: number
  costBasis: number
  avgCost: number | null
  currentPrice: number | null
  priceAsOf: string | null
  marketValue: number | null
  gainLoss: number | null
  gainLossPct: number | null
  dayGainLoss: number | null
  realizedGainLoss: number | null
  targetPrice: number | null
  targetDirection: TargetDirection
  alertEnabled: boolean
  alertTriggeredAt: string | null
  distanceToTargetPct: number | null
  needsOpeningBalance: boolean
  priceUnavailable: boolean
  heldInIra: boolean
  iraNote: string | null
}

export interface DashboardSummary {
  totalMarketValue: number
  totalCostBasis: number
  totalGainLoss: number
  totalGainLossPct: number | null
  dayGainLoss: number | null
  dayGainLossPct: number | null
  dayStartIsCustom: boolean
  unpricedCount: number
  deposited: number
  cash: number
  dividends: number
  realized: number
  ytdTaxRealized: number
  ytdRealized: number
  ytdWashAdjustment: number
  ytdEstimatedTax: number
  accountTotal: number
  pricesAsOf: string | null
  quotesObservedAt: string | null
  quotesStale: boolean
  quotesViaFallback: boolean
}

export interface Dashboard {
  rows: PositionRow[]
  summary: DashboardSummary
}

export interface LotDto {
  id: number
  shares: number
  totalCost: number
  remainingShares: number
  remainingCost: number
  tradeDate: string
  source: LotSource
  sourceTxId: number | null
  longTermOn: string
  daysToLongTerm: number | null
}

export interface SaleDto {
  id: number
  tradeDate: string
  sharesSold: number
  proceeds: number
  costBasisConsumed: number
  realizedGainLoss: number
  unmatchedShares: number
}

export interface PositionAccountDetail {
  positionId: number
  accountId: number
  account: string
  treatmentLabel: string
  isRetirement: boolean
  needsOpeningBalance: boolean
  hasImportedTransactions: boolean
  lots: LotDto[]
  sales: SaleDto[]
}

export interface PositionDetail {
  ticker: string
  cusip: string | null
  name: string | null
  accounts: PositionAccountDetail[]
}

export interface QuotePush {
  ticker: string
  price: number
  prevClose: number | null
  fetchedAt: string
}

export interface AlertPush {
  kind: 'position' | 'watchlist'
  ticker: string
  price: number
  targetPrice: number
  message: string
}

export interface TickerTarget {
  id: number
  ticker: string
  targetPrice: number | null
  targetDirection: TargetDirection
  alertEnabled: boolean
  alertTriggeredAt: string | null
  notes: string | null
}

export interface WatchlistItem {
  id: number
  ticker: string
  name: string | null
  addedAt: string
  thesis: string | null
  conviction: number
  targetBuyPrice: number | null
  alertEnabled: boolean
  alertTriggeredAt: string | null
  notes: string | null
  currentPrice: number | null
  distanceToTargetPct: number | null
  dayChange: number | null
  dayChangePct: number | null
}

export interface SymbolSearchResult {
  symbol: string
  description: string
  type: string
}

export interface SymbolSearchResponse {
  results: SymbolSearchResult[]
  error: string | null
}

export interface QuoteDetail {
  ticker: string
  current: number
  prevClose: number
  fetchedAt: string
  dayChange: number
  dayChangePct: number | null
}

export interface CompanyProfile {
  ticker: string
  name: string | null
  industry: string | null
  marketCapMillions: number | null
  ipoDate: string | null
  webUrl: string | null
}

export interface FundamentalMetrics {
  peTtm: number | null
  ps: number | null
  pb: number | null
  epsTtm: number | null
  revenueGrowthYoY: number | null
  revenueGrowth3Y: number | null
  epsGrowthYoY: number | null
  grossMargin: number | null
  netMargin: number | null
  roe: number | null
  beta: number | null
  high52: number | null
  low52: number | null
}

export interface RecommendationTrend {
  period: string
  strongBuy: number
  buy: number
  hold: number
  sell: number
  strongSell: number
}

export interface EarningsQuarter {
  period: string
  estimate: number | null
  actual: number | null
  surprisePercent: number | null
}

export interface NewsItem {
  publishedAt: string
  source: string
  headline: string
  url: string
}

/// GET /api/research/{ticker}/{panel}: panels load and degrade independently —
/// a null value plus error string means that panel (only) is unavailable.
export interface PanelResponse<T> {
  value: T | null
  error: string | null
}

export interface SnapshotValue {
  quote: QuoteDetail | null
  profile: CompanyProfile | null
}
export interface FundamentalsValue {
  metrics: FundamentalMetrics | null
}
export interface RecommendationsValue {
  trends: RecommendationTrend[]
}
export interface EarningsValue {
  quarters: EarningsQuarter[]
  nextEarningsDate: string | null
}
export interface NewsValue {
  items: NewsItem[]
}
export interface PriceBar {
  date: string // yyyy-MM-dd
  close: number
}
/// All-time daily closes, oldest-first; may be stale warehouse bars (with the
/// panel error set) when the live history provider is down.
export interface HistoryValue {
  bars: PriceBar[]
}

// ---- taxes (/api/tax/{year}) -----------------------------------------------

export type HoldingTerm = 'ShortTerm' | 'LongTerm'
export type FilingStatus = 'Single' | 'MarriedFilingJointly' | 'HeadOfHousehold'

export interface TaxEvent {
  ticker: string
  account: string
  accountId: number
  acquired: string
  sold: string
  daysHeld: number
  term: HoldingTerm
  shares: number
  proceeds: number
  basis: number
  gainLoss: number
  isWash: boolean
  disallowed: number
  crossAccountDisallowed: number
  iraDisallowed: number
  hasCrossAccountWash: boolean
  hasIraWash: boolean
}

export interface TaxNetting {
  netShortTerm: number
  netLongTerm: number
  taxableShortTerm: number
  taxableLongTerm: number
  ordinaryOffset: number
  carryforwardShortTerm: number
  carryforwardLongTerm: number
}

export interface TaxEstimate {
  taxWithInvestments: number
  taxWithoutInvestments: number
  attributable: number
}

export interface TaxDividend {
  ticker: string
  amount: number
}

export interface TaxYearData {
  year: number
  availableYears: number[]
  events: TaxEvent[]
  yearRealized: number
  yearShortTerm: number
  yearLongTerm: number
  carryInShortTerm: number
  carryInLongTerm: number
  carryInIsManual: boolean
  netting: TaxNetting
  totalDisallowed: number
  permanentlyLost: number
  grossDividends: number
  dividendsByTicker: TaxDividend[]
  estimate: TaxEstimate
  bracketsAreFallback: boolean
  retirementAccountsExcluded: number
}

export interface WashRisk {
  ticker: string
  lossAtRisk: number
  latestSale: string
  safeToBuyOn: string
  daysToWait: number
}

// ---- sell simulator (/api/simulator/sell) ----------------------------------

export interface SimPortion {
  acquired: string
  shares: number
  term: HoldingTerm
  basis: number
  proceeds: number
  gainLoss: number
  longTermOn: string
}

export interface SimCallout {
  shares: number
  days: number
  date: string
  estimatedSavings: number
}

export interface SellSimulation {
  portions: SimPortion[]
  shortTermGainLoss: number
  longTermGainLoss: number
  totalProceeds: number
  estimatedTaxOnSale: number
  washWarning: string | null
  iraWashWarning: string | null
  longTermCallouts: SimCallout[]
  sharesRequested: number
  sharesAvailable: number
}

// ---- sector analysis / universes / crawler / signals ------------------------

export interface UniverseInfo {
  id: number
  name: string
  isSeeded: boolean
  memberCount: number
}

export interface CrawlerStatus {
  running: boolean
  processed: number
  total: number
  currentTicker: string | null
  lastCompletedAt: string | null
  lastError: string | null
  totalTickers: number
  freshTickers: number
  staleTickers: number
  pendingItems: number
  budgetPerMinute: number
  callsLastMinute: number
}

export interface SectorInfo {
  sector: string
  tickerCount: number
  withMetrics: number
  withHistory: number
  oldestMetricsAsOf: string | null
  newestMetricsAsOf: string | null
}

export interface TickerReturn {
  ticker: string
  return20d: number
}

export interface SectorSummary {
  sector: string
  tickerCount: number
  withMetrics: number
  withHistory: number
  medianPe: number | null
  medianPs: number | null
  medianPb: number | null
  medianRevenueGrowth: number | null
  avgPctOff52wHigh: number | null
  breadthAboveSma50: number | null
  breadthAboveSma200: number | null
  medianReturn20d: number | null
  top5By20d: TickerReturn[]
  bottom5By20d: TickerReturn[]
  oldestMetricsAsOf: string | null
  newestMetricsAsOf: string | null
}

export interface SectorTickerRow {
  ticker: string
  marketCap: number | null
  pe: number | null
  ps: number | null
  pb: number | null
  revenueGrowthYoY: number | null
  grossMargin: number | null
  netMargin: number | null
  roe: number | null
  pctOff52wHigh: number | null
  return20d: number | null
  zPe: number | null
  zGrowth: number | null
  zGrossMargin: number | null
  zNetMargin: number | null
  zRoe: number | null
  zPctOff52wHigh: number | null
  zReturn20d: number | null
  metricsAsOf: string | null
  priceDate: string | null
}

export interface SectorHeatmapRow {
  sector: string
  tickerCount: number
  medianReturn20d: number | null
  breadthAboveSma50: number | null
  medianPctOff52wHigh: number | null
}

export interface SignalHitDto {
  id: number
  ticker: string
  signalKind: string
  firedAt: string
  dismissed: boolean
  sector: string | null
  conditions: SignalCondition[]
  summary: string
  /// 0-100 margin beyond the rule's thresholds — ranks hits against each
  /// other; not a predicted return.
  strength: number | null
}

export interface SignalCondition {
  label: string
  observed: string
  threshold: string
  pass: boolean
}

export interface SectorMetricContext {
  key: string
  label: string
  value: number | null
  sectorMedian: number | null
  percentile: number | null
  isPercent: boolean
}

export interface SectorContextResponse {
  sector: string | null
  coverageCount: number
  asOf: string | null
  metrics: SectorMetricContext[]
}

// ---- research queue / discoveries / scoreboard / AI briefs -------------------

export type EvidenceStatus = 'Pass' | 'Fail' | 'Neutral' | 'NotAvailable'

export interface QueueEvidence {
  dimension: string
  label: string
  status: EvidenceStatus
  observed: string
  threshold: string
  asOf: string | null
  weight: number
  appliedWeight: number
  note: string | null
}

export interface QueueRow {
  ticker: string
  sector: string | null
  /// weightedPasses / applicableWeight — a normalized confluence ratio, shown
  /// only alongside its full evidence breakdown.
  score: number
  weightedPasses: number
  applicableWeight: number
  fundamentalsRank: number | null
  isHeld: boolean
  onWatchlist: boolean
  fitLine: string
  latestBriefId: number | null
  evidence: QueueEvidence[]
}

export interface SectorWeight {
  sector: string
  weightPct: number
}

export interface QueueResponse {
  rows: QueueRow[]
  portfolioSectorWeights: SectorWeight[]
  holdingsCount: number
  snoozedCount: number
  jobsLastRanAt: string | null
  downWeightingActive: boolean
}

export interface Discovery {
  ticker: string
  sourceTicker: string
  discoveredAt: string
}

export interface ScoreboardExample {
  ticker: string
  fireDate: string
  excess: number
}

export interface ScoreboardHorizon {
  days: number
  matured: number
  medianExcess: number | null
  beatRatePct: number | null
  best: ScoreboardExample | null
  worst: ScoreboardExample | null
  sufficient: boolean
}

export interface ScoreboardRow {
  signalKind: string
  fires: number
  dismissed: number
  dismissRatePct: number | null
  horizons: ScoreboardHorizon[]
  downWeighted: boolean
  downWeightReason: string | null
}

export interface ScoreboardResponse {
  rows: ScoreboardRow[]
  downWeightingEnabled: boolean
  lastJobsRunAt: string | null
}

export interface ResearchBrief {
  id: number
  ticker: string
  createdAt: string
  model: string
  briefMarkdown: string
  tokensUsed: number
  /// A section failed grounding checks (or the run aborted); its text was
  /// withheld and the generation trace holds the evidence.
  needsReview: boolean
  /// The exact payload the pipeline sliced per section; present only on
  /// GET /api/briefs/{id}.
  inputSnapshotJson: string | null
}

export type AiProvider = 'openwebui' | 'anthropic' | 'disabled'

export interface AiUsage {
  month: string
  tokensUsed: number
  budget: number
  enabled: boolean
  provider: AiProvider
  /// The monthly ceiling is an Anthropic-cost control; the local server has
  /// no per-token cost, so usage is logged but never enforced there.
  budgetEnforced: boolean
}

/// One row of the Briefs history tab — bodies load per brief on open.
export interface BriefSummary {
  id: number
  ticker: string
  createdAt: string
  model: string
  tokensUsed: number
  needsReview: boolean
}

/// Polled from GET /api/briefs/status while the BriefAgent pipeline runs.
export interface BriefRunStatus {
  active: boolean
  ticker: string | null
  stage: string | null
  sectionIndex: number
  totalSections: number
  startedAt: string | null
  lastError: string | null
  lastBriefId: number | null
}

export interface BriefTraceEntry {
  id: number
  section: string
  stage: string
  userPrompt: string
  rawResponse: string
  cleanedText: string
  violationsJson: string
  passed: boolean
  createdAt: string
}
