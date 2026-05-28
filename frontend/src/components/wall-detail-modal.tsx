import { useEffect } from 'react'
import { createPortal } from 'react-dom'
import { ExternalLink, X } from 'lucide-react'
import { useQuery } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { api, type MarketDepth, type OrderBookWallDto } from '@/lib/api'

/**
 * Modal that pops up when a user clicks an order-book wall.
 *
 * Left column  (60%): TradingView official chart widget for BINANCE:{symbol}.
 * Right column (40%): live order-book ladder rendered from /api/market/depth.
 *                     The wall row the user clicked is highlighted so they can
 *                     spot it instantly in the surrounding liquidity context.
 *
 * Why TV widget instead of screenshotting Binance: Binance has no public
 * snapshot endpoint and headless-browser scraping is fragile + against ToS.
 * TV's `widgetembed` is the officially supported, free, instant alternative.
 */
export function WallDetailModal({ wall, onClose }: { wall: OrderBookWallDto; onClose: () => void }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    document.addEventListener('keydown', onKey)
    document.body.style.overflow = 'hidden'
    return () => {
      document.removeEventListener('keydown', onKey)
      document.body.style.overflow = ''
    }
  }, [onClose])

  const { data: depth, isFetching } = useQuery({
    queryKey: ['market', 'depth', wall.symbol],
    queryFn: () => api<MarketDepth>(`/api/market/depth?symbol=${wall.symbol}&limit=50`),
    refetchInterval: 5_000,
    staleTime: 3_000,
  })

  const tvUrl = `https://s.tradingview.com/widgetembed/?frameElementId=tv-${wall.symbol}&symbol=BINANCE:${wall.symbol}&interval=15&hidesidetoolbar=1&theme=dark&style=1&timezone=Etc%2FUTC&studies=&hideideas=1&saveimage=0&toolbarbg=151515`
  const binanceUrl = wall.symbol.endsWith('USDT')
    ? `https://www.binance.com/en/trade/${wall.symbol.slice(0, -4)}_USDT?type=spot`
    : `https://www.binance.com/en/trade/${wall.symbol}?type=spot`

  return createPortal(
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4 backdrop-blur-sm"
      onClick={onClose}
    >
      <div
        className="relative flex h-[80vh] w-[min(1200px,95vw)] flex-col overflow-hidden rounded-lg border border-border bg-surface shadow-2xl"
        onClick={e => e.stopPropagation()}
      >
        <header className="flex items-center justify-between border-b border-border/40 px-4 py-2">
          <div className="flex items-center gap-3">
            <span className="font-mono text-base font-semibold">{wall.symbol}</span>
            <span className={`font-mono text-xs uppercase ${wall.side === 'Bid' ? 'text-up' : 'text-down'}`}>
              {wall.side === 'Bid' ? 'BID · support' : 'ASK · resistance'}
            </span>
            <span className="font-mono text-xs text-muted-foreground">
              @ {fmtPrice(wall.price)} · ${fmtBigUsdt(wall.quoteNotional)} · {wall.multiplier.toFixed(1)}× avg
            </span>
          </div>
          <div className="flex items-center gap-1">
            <a
              href={binanceUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex h-7 items-center gap-1 rounded-sm border border-border bg-surface-2 px-2 text-xs hover:border-primary/40"
            >
              <ExternalLink className="h-3 w-3" /> Binance
            </a>
            <Button size="sm" variant="outline" className="h-7 w-7 p-0" onClick={onClose}>
              <X className="h-3.5 w-3.5" />
            </Button>
          </div>
        </header>

        <div className="grid flex-1 grid-cols-[60%_40%] overflow-hidden">
          <div className="border-r border-border/40 bg-black/30">
            <iframe
              title={`TV ${wall.symbol}`}
              src={tvUrl}
              className="h-full w-full"
              frameBorder="0"
              allowTransparency
              allowFullScreen
            />
          </div>
          <div className="flex flex-col overflow-hidden">
            <div className="border-b border-border/40 px-3 py-1.5 text-[11px] text-muted-foreground">
              Order book · refresh 5s {isFetching && '· loading…'}
            </div>
            <DepthLadder depth={depth} wall={wall} />
          </div>
        </div>
      </div>
    </div>,
    document.body,
  )
}

function DepthLadder({ depth, wall }: { depth?: MarketDepth; wall: OrderBookWallDto }) {
  if (!depth) {
    return <div className="flex h-full items-center justify-center text-xs text-muted-foreground">loading depth…</div>
  }
  // Asks descending so worst (highest) ask is at top, best ask just above mid — matches Binance UI.
  const asks = [...depth.asks].slice(0, 15).reverse()
  const bids = depth.bids.slice(0, 15)
  const maxNotional = Math.max(
    ...asks.map(l => l.notional),
    ...bids.map(l => l.notional),
    1,
  )
  const isWall = (side: 'Bid' | 'Ask', price: number) => side === wall.side && Math.abs(price - wall.price) / wall.price < 0.0005

  return (
    <div className="flex-1 overflow-y-auto font-mono text-[11px]">
      <Row label="PRICE" qty="QTY" notional="$ NOTIONAL" header />
      {asks.map(l => (
        <Row
          key={`a-${l.price}`}
          side="ask"
          label={fmtPrice(l.price)}
          qty={fmtQty(l.qty)}
          notional={fmtBigUsdt(l.notional)}
          fillPct={(l.notional / maxNotional) * 100}
          highlight={isWall('Ask', l.price)}
        />
      ))}
      <div className="border-y border-border/60 bg-surface-2 px-2 py-1 text-center font-mono text-xs">
        mid {fmtPrice(wall.midPrice)}
      </div>
      {bids.map(l => (
        <Row
          key={`b-${l.price}`}
          side="bid"
          label={fmtPrice(l.price)}
          qty={fmtQty(l.qty)}
          notional={fmtBigUsdt(l.notional)}
          fillPct={(l.notional / maxNotional) * 100}
          highlight={isWall('Bid', l.price)}
        />
      ))}
    </div>
  )
}

function Row({
  side, label, qty, notional, fillPct, highlight, header,
}: {
  side?: 'bid' | 'ask'
  label: string
  qty: string
  notional: string
  fillPct?: number
  highlight?: boolean
  header?: boolean
}) {
  const colorText = side === 'ask' ? 'text-down' : side === 'bid' ? 'text-up' : 'text-muted-foreground'
  const bgFill = side === 'ask' ? 'bg-down/15' : 'bg-up/15'
  return (
    <div className={`relative grid grid-cols-3 gap-2 px-2 py-0.5 ${header ? 'border-b border-border/40 text-[10px] uppercase tracking-wider text-muted-foreground' : ''} ${highlight ? 'ring-1 ring-inset ring-primary bg-primary/10' : ''}`}>
      {fillPct !== undefined && (
        <div
          className={`absolute inset-y-0 right-0 ${bgFill}`}
          style={{ width: `${Math.min(100, fillPct)}%` }}
        />
      )}
      <span className={`relative ${colorText}`}>{label}{highlight && <span className="ml-1 rounded-sm bg-primary px-1 text-[9px] uppercase text-primary-foreground">wall</span>}</span>
      <span className="relative text-right">{qty}</span>
      <span className="relative text-right">{notional}</span>
    </div>
  )
}

function fmtPrice(n: number): string {
  if (n >= 1000) return n.toLocaleString('en-US', { maximumFractionDigits: 2 })
  if (n >= 1) return n.toFixed(4)
  return n.toPrecision(4)
}

function fmtQty(n: number): string {
  if (n >= 1e6) return `${(n / 1e6).toFixed(2)}M`
  if (n >= 1e3) return `${(n / 1e3).toFixed(2)}K`
  return n.toFixed(2)
}

function fmtBigUsdt(n: number): string {
  if (!n || !isFinite(n)) return '0'
  if (n >= 1e9) return `${(n / 1e9).toFixed(2)}B`
  if (n >= 1e6) return `${(n / 1e6).toFixed(2)}M`
  if (n >= 1e3) return `${(n / 1e3).toFixed(2)}K`
  return n.toFixed(0)
}
