export const API_URL = import.meta.env.VITE_API_URL ?? 'http://localhost:5087'
export const HUB_URL = import.meta.env.VITE_HUB_URL ?? 'http://localhost:5087/hubs/market'

const TOKEN_KEY = 'qfb.token'

export function getToken(): string | null {
  if (typeof window === 'undefined') return null
  return window.localStorage.getItem(TOKEN_KEY)
}

export function setToken(token: string | null) {
  if (typeof window === 'undefined') return
  if (token) window.localStorage.setItem(TOKEN_KEY, token)
  else window.localStorage.removeItem(TOKEN_KEY)
}

export class ApiError extends Error {
  constructor(public status: number, message: string, public payload?: unknown) {
    super(message)
  }
}

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = getToken()
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json')
  if (init.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  if (token) headers.set('Authorization', `Bearer ${token}`)

  let res: Response
  try {
    res = await fetch(`${API_URL}${path}`, { ...init, headers, cache: 'no-store' })
  } catch (networkErr) {
    if (init.method && init.method !== 'GET') throw networkErr
    await new Promise(r => setTimeout(r, 1500))
    res = await fetch(`${API_URL}${path}`, { ...init, headers, cache: 'no-store' })
  }
  const text = await res.text()
  const data = text ? safeJson(text) : null

  if (res.status === 401 && token && typeof window !== 'undefined' && !path.startsWith('/api/auth/')) {
    setToken(null)
    window.location.href = '/login'
    throw new ApiError(401, 'Session expired, please login again.', data)
  }

  if (!res.ok) {
    const msg = (data && typeof data === 'object' && 'errors' in data)
      ? JSON.stringify((data as { errors: unknown }).errors)
      : (data && typeof data === 'object' && 'message' in data && typeof (data as { message: unknown }).message === 'string')
      ? (data as { message: string }).message
      : res.statusText
    throw new ApiError(res.status, msg, data)
  }

  return data as T
}

function safeJson(text: string): unknown {
  try { return JSON.parse(text) } catch { return text }
}

export type AuthResponse = {
  accessToken: string
  userId: string
  email: string
  displayName: string
}

export type MarketTicker = {
  symbol: string
  lastPrice: number
  priceChangePercent: number
  quoteVolume: number
  at: string
}

export type MarketOverview = {
  updatedAt: string
  topGainers: MarketTicker[]
  topVolume: MarketTicker[]
}

export type AccountStats = {
  openPositions: number
  todayPnl: number
}

export type SymbolRow = {
  id: number
  code: string
  baseAsset: string
  quoteAsset: string
}

export type NewListingDto = {
  code: string
  baseAsset: string
  listedAt: string
  price: number
  priceChangePercent: number
  quoteVolume: number
}

export type StrategyDto = {
  id: string
  name: string
  kind: string
  parametersJson: string
  description?: string | null
  createdAt: string
}

export type ExchangeDto = {
  id: number
  code: string
  name: string
  restBaseUrl: string
  webSocketBaseUrl: string
}

export type ApiKeyDto = {
  id: string
  exchangeId: number
  exchangeCode: string
  label: string
  keyPreview: string
  mode: string
  isActive: boolean
  permissionsJson: string
  lastValidatedAt?: string | null
  lastUsedAt?: string | null
  lastError?: string | null
  createdAt: string
  updatedAt: string
}

export type BotRunMode = 'Off' | 'ScanOnly' | 'PaperTrading' | 'LiveTrading'
export type BotKind = 'Signal' | 'Dca' | 'Grid' | 'Scalp'

export type BotDto = {
  id: string
  name: string
  mode: string
  state: string
  kind: BotKind
  kindConfigJson?: string | null
  apiKeyId?: string | null
  leverage: number
  runMode: BotRunMode
  symbolFilterJson?: string | null
  strategyId: string
  strategyKind?: string | null
  symbolId: number
  symbolCode: string
  baseEquityUsdt: number
  maxPositionSize: number
  riskPerTradePercent?: number | null
  dailyLossStopPercent: number
  maxOpenPositions: number
  maxConsecutiveLosses: number
  cooldownAfterLossMinutes: number
  killSwitchEnabled: boolean
  killSwitchTrippedAt?: string | null
  killSwitchReason?: string | null
  stopLossKind: 'FixedPercent' | 'Atr' | string
  defaultStopLossPercent?: number | null
  atrPeriod: number
  atrMultiplier: number
  defaultTakeProfitPercent?: number | null
  takeProfitLevelsJson?: string | null
  defaultTrailingStopPercent?: number | null
  breakEvenEnabled: boolean
  breakEvenTriggerPercent?: number | null
  breakEvenOffsetPercent: number
  lastError?: string | null
  createdAt: string
}

export type TpLevelInput = { profitPercent: number; closePercent: number }
export type TpLevelState = TpLevelInput & {
  closePrice: number
  closeQty: number
  hitAt?: string | null
}

export type RiskEventDto = {
  id: string
  botId?: string | null
  eventType: string
  severity: 'info' | 'warn' | 'critical' | string
  message: string
  actionTaken?: string | null
  createdAt: string
}

export type OrderDto = {
  id: string
  side: string
  status: string
  price: number
  quantity: number
  commission: number
  realizedPnl: number
  createdAt: string
}

export type PositionDto = {
  id: string
  side: string
  status: string
  quantity: number
  originalQuantity: number
  entryPrice: number
  exitPrice?: number | null
  stopLossPrice?: number | null
  takeProfitPrice?: number | null
  trailingStopPercent?: number | null
  takeProfitLevelsJson?: string | null
  breakEvenTriggered: boolean
  realizedPnl: number
  closeReason?: string | null
  openedAt: string
  closedAt?: string | null
}

export type SignalDto = {
  id: string
  type: string
  side?: string | null
  price: number
  score: number
  payloadJson: string
  generatedAt: string
}

export type EquityPoint = { at: string; equity: number }

export type BacktestSummary = {
  id: string
  strategyId: string
  strategyKind: string
  symbolCode: string
  interval: string
  fromTime: string
  toTime: string
  initialCapital: number
  status: string
  finalEquity: number | null
  returnPercent: number | null
  maxDrawdownPercent: number | null
  sharpeRatio: number | null
  tradeCount: number | null
  winRatePercent: number | null
  createdAt: string
  completedAt: string | null
  error: string | null
}

export type BacktestDetail = { summary: BacktestSummary; equityCurve: EquityPoint[] }

export type ScannerResult = {
  symbol: string
  price: number
  priceChangePercent: number
  quoteVolume: number
}

export type ScannerResponse = {
  updatedAt: string
  filter: { minVolume: number; minPct: number; maxPct: number; windowSize: string; direction: 'any' | 'up' | 'down'; excludeCount: number; includeCount: number; maxSymbols: number }
  stages?: {
    totalUsdtPairs: number
    afterVolume: number
    afterPctRange: number
    afterBlacklist: number
    afterWhitelist: number
  }
  count: number
  results: ScannerResult[]
  nearMissPct?: {
    maxAbsPctSeen: number
    samples: ScannerResult[]
  } | null
}

export type UserSettingsDto = {
  telegramAlertsEnabled: boolean
  telegramBotTokenConfigured: boolean
  telegramChatId?: string | null
  whaleAlertEnabled: boolean
  whaleAlertBotTokenConfigured: boolean
  whaleAlertChatId?: string | null
  whaleAlertIntervals?: string | null
  whaleAlertMultiplier: number
  whaleAlertMinVolume24h: number
  whaleAlertMode: 'intrabar' | 'candle_close'
  whaleAlertCooldownMinutes: number
  whaleAlertDirection: 'buy' | 'sell' | 'both'
  whaleAlertLookback: number
  wallAlertEnabled: boolean
  wallAlertBotTokenConfigured: boolean
  wallAlertChatId?: string | null
  wallAlertMinNotional: number
  wallAlertMaxDistancePct: number
  wallAlertSide: '' | 'Bid' | 'Ask'
  wallAlertCooldownMinutes: number
  updatedAt: string
}

export type BotStatsDto = {
  botId: string
  name: string
  baseEquity: number
  currentEquity: number
  totalRealizedPnl: number
  unrealizedPnl: number
  totalReturnPercent: number
  totalTrades: number
  winningTrades: number
  losingTrades: number
  openPositions: number
  winRatePercent: number
  averageWin: number
  averageLoss: number
  largestWin: number
  largestLoss: number
  profitFactor: number
  maxDrawdownPercent: number
  expectancy: number
  pnlToday: number
  pnl7d: number
  pnl30d: number
  tradesToday: number
  firstTradeAt: string | null
  lastTradeAt: string | null
  equityCurve: { at: string; equity: number }[]
}

export type BotStatsRowDto = {
  botId: string
  name: string
  symbolCode: string
  runMode: string
  state: string
  baseEquity: number
  currentEquity: number
  totalRealizedPnl: number
  totalReturnPercent: number
  totalTrades: number
  winRatePercent: number
  maxDrawdownPercent: number
  pnlToday: number
  pnl7d: number
  openPositions: number
}

export type OrderBookWallDto = {
  symbol: string
  side: 'Bid' | 'Ask'
  price: number
  quantity: number
  quoteNotional: number
  midPrice: number
  distanceFromMidPercent: number
  multiplier: number
  at: string
}

export type OrderBookWallsResponse = {
  updatedAt: string
  filter: { minNotional: number; maxDistancePct: number; side: string; limit: number }
  defaults: { minNotional: number; maxDistancePct: number; scanIntervalSeconds: number; maxSymbols: number }
  totalCached: number
  count: number
  results: OrderBookWallDto[]
}

export type DepthLevel = { price: number; qty: number; notional: number }
export type MarketDepth = {
  symbol: string
  at: string
  bids: DepthLevel[]
  asks: DepthLevel[]
}

export type FuturesPositionSnapshot =
  | {
      symbol: string
      positionAmt: number
      entryPrice: number
      markPrice: number
      unrealizedProfit: number
      liquidationPrice: number
      leverage: number
    }
  | {
      kind: 'spot'
      canTrade: boolean
      canWithdraw: boolean
      baseAsset: string
      baseFree: number
      baseLocked: number
      quoteAsset: string
      quoteFree: number
      quoteLocked: number
    }
  | { error: string }

export type SentimentEventDto = {
  id: string
  symbolCode: string
  source: string
  headline: string
  url?: string | null
  score: number
  magnitude: number
  tags?: string | null
  at: string
  ingestedAt: string
}

export type SentimentSnapshotDto = {
  symbolCode: string
  rollingScore: number
  rollingMagnitude: number
  sampleCount: number
  latestScore: number | null
  latestAt: string | null
}

export type ScoredSentiment = {
  symbolCode: string
  source: string
  headline: string
  url?: string | null
  score: number
  magnitude: number
  at: string
  tags?: string | null
}

export type CandleData = {
  symbol: string
  interval: string
  openTime: string
  closeTime: string
  open: number
  high: number
  low: number
  close: number
  volume: number
  quoteVolume: number
  tradeCount: number
  isClosed: boolean
}
