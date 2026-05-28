/**
 * Centralized TanStack Query keys.
 *
 * Why a factory: keys are referenced both at the call site (useQuery) and
 * from the SignalR → invalidation bridge. Using a single source of truth
 * avoids drift (e.g. `['bot-orders', id]` here vs `['bots', id, 'orders']`
 * there would silently break realtime updates).
 *
 * Keys here MUST match those invalidated in `signalr-query-bridge.tsx`.
 */
export const qk = {
  // Market data
  marketOverview: ['market', 'overview'] as const,
  newListings: (limit: number) => ['market', 'new-listings', limit] as const,
  orderBookWalls: (params: OrderBookWallParams) => ['market', 'order-book-walls', params] as const,
  scanner: (params: ScannerParams) => ['market', 'scanner', params] as const,

  // Bots
  bots: ['bots'] as const,
  bot: (id: string) => ['bot', id] as const,
  botOrders: (id: string) => ['bot-orders', id] as const,
  botPositions: (id: string) => ['bot-positions', id] as const,
  botSignals: (id: string) => ['bot-signals', id] as const,
  botRiskEvents: (id: string) => ['bot-risk-events', id] as const,
  botStats: (id: string) => ['bot-stats', id] as const,
  botsStatsSummary: ['bots-stats-summary'] as const,

  // Strategies / backtests
  strategies: ['strategies'] as const,
  backtests: ['backtests'] as const,
  backtest: (id: string) => ['backtest', id] as const,

  // Settings
  exchanges: ['exchanges'] as const,
  apiKeys: ['api-keys'] as const,
  userSettings: ['user-settings'] as const,

  // Sentiment
  sentimentRecent: ['sentiment', 'recent'] as const,
  sentimentTop: (n: number, direction: 'bull' | 'bear') => ['sentiment', 'top', n, direction] as const,
}

export type ScannerParams = {
  minVolume: string
  minPct: string
  maxPct: string
  windowSize: string
  direction: 'any' | 'up' | 'down'
  maxSymbols: string
  exclude: string
}

export type OrderBookWallParams = {
  minNotional: string
  maxDistancePct: string
  side: '' | 'Bid' | 'Ask'
  limit: string
}
