import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { createContext, useContext, useEffect, useRef, useState } from 'react'
import { HUB_URL } from './api'
import { useAuth } from './auth-context'

export type TickerEvent = {
  exchange: string
  symbol: string
  price: number
  priceChangePercent: number
  quoteVolume: number
  at: string
}

export type KlineEvent = {
  exchange: string
  symbol: string
  candle: {
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
  at: string
}

type SignalRState = {
  connection: HubConnection | null
  state: HubConnectionState
  subscribeTicker: (handler: (e: TickerEvent) => void) => () => void
  subscribeKline: (handler: (e: KlineEvent) => void) => () => void
}

const SignalRContext = createContext<SignalRState | null>(null)

export function SignalRProvider({ children }: { children: React.ReactNode }) {
  const { token } = useAuth()
  const [connection, setConnection] = useState<HubConnection | null>(null)
  const [state, setState] = useState<HubConnectionState>(HubConnectionState.Disconnected)
  const tickerHandlers = useRef(new Set<(e: TickerEvent) => void>())
  const klineHandlers = useRef(new Set<(e: KlineEvent) => void>())

  useEffect(() => {
    if (!token) {
      setConnection(null)
      setState(HubConnectionState.Disconnected)
      return
    }

    const conn = new HubConnectionBuilder()
      .withUrl(HUB_URL, { accessTokenFactory: () => token })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    conn.on('ticker', (e: TickerEvent) => tickerHandlers.current.forEach(h => h(e)))
    conn.on('kline', (e: KlineEvent) => klineHandlers.current.forEach(h => h(e)))
    conn.onreconnecting(() => setState(HubConnectionState.Reconnecting))
    conn.onreconnected(() => setState(HubConnectionState.Connected))
    conn.onclose(() => setState(HubConnectionState.Disconnected))

    let cancelled = false
    conn.start().then(() => {
      if (!cancelled) {
        setConnection(conn)
        setState(HubConnectionState.Connected)
      }
    }).catch((err) => {
      console.error('SignalR start failed', err)
      if (typeof err?.message === 'string' && err.message.includes('401') && typeof window !== 'undefined') {
        window.localStorage.removeItem('qfb.token')
        window.location.href = '/login'
      }
    })

    return () => {
      cancelled = true
      conn.stop().catch(() => undefined)
    }
  }, [token])

  const value: SignalRState = {
    connection,
    state,
    subscribeTicker: (handler) => {
      tickerHandlers.current.add(handler)
      return () => tickerHandlers.current.delete(handler) as unknown as void
    },
    subscribeKline: (handler) => {
      klineHandlers.current.add(handler)
      return () => klineHandlers.current.delete(handler) as unknown as void
    },
  }

  return <SignalRContext.Provider value={value}>{children}</SignalRContext.Provider>
}

export function useSignalR() {
  const ctx = useContext(SignalRContext)
  if (!ctx) throw new Error('useSignalR must be used inside SignalRProvider')
  return ctx
}
