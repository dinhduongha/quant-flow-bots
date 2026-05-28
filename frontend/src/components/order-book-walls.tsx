import { useEffect, useMemo, useRef, useState } from 'react'
import { Layers, Pause, RefreshCw, SlidersHorizontal } from 'lucide-react'
import { useQuery } from '@tanstack/react-query'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { api, type OrderBookWallDto, type OrderBookWallsResponse } from '@/lib/api'
import { qk, type OrderBookWallParams } from '@/lib/queries'
import { WallDetailModal } from '@/components/wall-detail-modal'

/**
 * Order-book wall scanner widget. Lists symbols whose single price level holds an
 * unusually large limit order (>= MinNotional). Worker re-scans top-N symbols every
 * ~60s; SignalR push freshens the query immediately as new walls are detected.
 *
 * Default threshold = $200M USDT but you can lower it inline to find smaller walls
 * (e.g. $10M is typical for BTC near the touch).
 */
type WallGroup = {
  symbol: string
  largest: OrderBookWallDto
  totalNotional: number
  extraCount: number  // number of walls in this symbol beyond `largest`
}

export function OrderBookWalls() {
  const [filter, setFilter] = useState<OrderBookWallParams>({
    minNotional: '500000',
    maxDistancePct: '2',
    side: '',
    limit: '15',
  })
  const [open, setOpen] = useState(false)
  const [page, setPage] = useState(0)
  const [selectedWall, setSelectedWall] = useState<OrderBookWallDto | null>(null)
  const [paused, setPaused] = useState(false)
  const resumeTimer = useRef<number | null>(null)
  const PAGE_SIZE = 10

  const queryString = useMemo(() => {
    const p = new URLSearchParams()
    if (filter.minNotional) p.set('minNotional', filter.minNotional)
    if (filter.maxDistancePct) p.set('maxDistancePct', filter.maxDistancePct)
    if (filter.side) p.set('side', filter.side)
    if (filter.limit) p.set('limit', filter.limit)
    return p.toString()
  }, [filter])

  const { data, isFetching, refetch } = useQuery({
    queryKey: qk.orderBookWalls(filter),
    queryFn: () => api<OrderBookWallsResponse>(`/api/market/order-book-walls?${queryString}`),
    refetchInterval: () => paused ? false : 30_000,
  })

  // Hover anywhere on the panel pauses auto-refetch so rows don't shuffle while reading.
  // 2s grace on leave so a brief mouse-out doesn't immediately fire a refetch.
  function onEnter() {
    if (resumeTimer.current) { window.clearTimeout(resumeTimer.current); resumeTimer.current = null }
    setPaused(true)
  }
  function onLeave() {
    if (resumeTimer.current) window.clearTimeout(resumeTimer.current)
    resumeTimer.current = window.setTimeout(() => setPaused(false), 2000)
  }

  const results = data?.results ?? []
  // Group walls by symbol; keep largest as the "header" row, badge count for extras.
  // Largest stays the canonical click target — the detail modal already loads the full ladder.
  const groups = useMemo<WallGroup[]>(() => {
    const byKey = new Map<string, WallGroup>()
    for (const w of results) {
      const g = byKey.get(w.symbol)
      if (!g) {
        byKey.set(w.symbol, { symbol: w.symbol, largest: w, totalNotional: w.quoteNotional, extraCount: 0 })
      } else {
        g.totalNotional += w.quoteNotional
        g.extraCount += 1
        if (w.quoteNotional > g.largest.quoteNotional) g.largest = w
      }
    }
    return Array.from(byKey.values()).sort((a, b) => b.largest.quoteNotional - a.largest.quoteNotional)
  }, [results])

  const totalPages = Math.max(1, Math.ceil(groups.length / PAGE_SIZE))
  const safePage = Math.min(page, totalPages - 1)
  const pageItems = groups.slice(safePage * PAGE_SIZE, (safePage + 1) * PAGE_SIZE)
  useEffect(() => { setPage(0) }, [filter.minNotional, filter.maxDistancePct, filter.side, filter.limit])

  return (
    <Card onMouseEnter={onEnter} onMouseLeave={onLeave}>
      <CardHeader className="flex flex-row items-center justify-between gap-2 pb-2">
        <div>
          <CardTitle className="flex items-center gap-2 text-sm">
            <Layers className="h-4 w-4 text-primary" /> Order-book Walls
            {paused && <Pause className="h-3 w-3 text-warning" aria-label="auto-refresh paused" />}
          </CardTitle>
          <p className="text-[11px] text-muted-foreground">
            {data ? `${groups.length} symbols · ${data.count}/${data.totalCached} walls · ≥ $${fmtBigUsdt(Number(filter.minNotional))} · |Δ| ≤ ${filter.maxDistancePct}%` : 'loading…'}
          </p>
        </div>
        <div className="flex items-center gap-1">
          <Button size="sm" variant="outline" className="h-6 px-2" onClick={() => setOpen(v => !v)} title="Filters">
            <SlidersHorizontal className="h-3.5 w-3.5" />
          </Button>
          <Button size="sm" variant="outline" className="h-6 px-2" onClick={() => void refetch()} title="Refresh">
            <RefreshCw className={`h-3.5 w-3.5 ${isFetching ? 'animate-spin' : ''}`} />
          </Button>
        </div>
      </CardHeader>

      {open && (
        <CardContent className="space-y-2 border-b border-border/40 px-3 pb-3 pt-0 text-xs">
          <FilterRow label="Min notional (USDT)">
            <input
              type="number"
              min={0}
              step={1000000}
              className="h-7 w-32 rounded-sm border border-border bg-surface px-2 font-mono text-xs outline-none focus:border-primary"
              value={filter.minNotional}
              onChange={e => setFilter(f => ({ ...f, minNotional: e.target.value }))}
            />
            <span className="font-mono text-[10px] text-muted-foreground">${fmtBigUsdt(Number(filter.minNotional) || 0)}</span>
          </FilterRow>
          <FilterRow label="Max distance from mid (%)">
            <input
              type="number"
              min={0}
              max={20}
              step={0.1}
              className="h-7 w-20 rounded-sm border border-border bg-surface px-2 font-mono text-xs outline-none focus:border-primary"
              value={filter.maxDistancePct}
              onChange={e => setFilter(f => ({ ...f, maxDistancePct: e.target.value }))}
            />
          </FilterRow>
          <FilterRow label="Side">
            <select
              className="h-7 rounded-sm border border-border bg-surface px-2 text-xs outline-none focus:border-primary"
              value={filter.side}
              onChange={e => setFilter(f => ({ ...f, side: e.target.value as OrderBookWallParams['side'] }))}
            >
              <option value="">Both</option>
              <option value="Bid">Bid (support)</option>
              <option value="Ask">Ask (resistance)</option>
            </select>
          </FilterRow>
          <FilterRow label="Limit">
            <input
              type="number"
              min={1}
              max={100}
              step={1}
              className="h-7 w-16 rounded-sm border border-border bg-surface px-2 font-mono text-xs outline-none focus:border-primary"
              value={filter.limit}
              onChange={e => setFilter(f => ({ ...f, limit: e.target.value }))}
            />
          </FilterRow>
          {data && (
            <p className="pt-1 text-[10px] text-muted-foreground">
              Worker scans top {data.defaults.maxSymbols} symbols every {data.defaults.scanIntervalSeconds}s.
              Default detection ≥ ${fmtBigUsdt(data.defaults.minNotional)}.
            </p>
          )}
        </CardContent>
      )}

      <CardContent className="space-y-1 px-3 pb-3 pt-2">
        {results.length === 0 && (
          <p className="text-[11px] text-muted-foreground">
            No walls match. Try lowering Min notional (e.g. <button className="text-primary hover:underline" onClick={() => setFilter(f => ({ ...f, minNotional: '10000000' }))}>$10M</button>) or widening |Δ|.
          </p>
        )}
        {pageItems.map(g => {
          const w = g.largest
          return (
            <button
              key={g.symbol}
              type="button"
              onClick={() => setSelectedWall(w)}
              className="flex w-full items-center justify-between gap-2 rounded-sm border border-border/40 bg-surface px-2 py-1.5 text-[11px] hover:border-primary/40 hover:bg-surface-2"
              title={g.extraCount > 0
                ? `Largest of ${g.extraCount + 1} walls on ${g.symbol} · total $${fmtBigUsdt(g.totalNotional)}`
                : `Inspect ${g.symbol} · ${w.multiplier.toFixed(1)}× avg level`}
            >
              <div className="flex min-w-0 items-center gap-2">
                <span className={`h-1.5 w-1.5 shrink-0 rounded-full ${w.side === 'Bid' ? 'bg-up' : 'bg-down'}`} />
                <span className="font-mono font-medium">{g.symbol}</span>
                <span className={`font-mono text-[10px] uppercase ${w.side === 'Bid' ? 'text-up' : 'text-down'}`}>{w.side === 'Bid' ? 'BUY' : 'SELL'}</span>
                <span className="num text-muted-foreground">@{fmtPrice(w.price)}</span>
                {g.extraCount > 0 && (
                  <span className="rounded-sm bg-surface-2 px-1 font-mono text-[9px] text-muted-foreground" title={`${g.extraCount} more wall${g.extraCount > 1 ? 's' : ''} on this symbol`}>+{g.extraCount}</span>
                )}
              </div>
              <div className="flex items-center gap-2">
                <span className="num font-medium">${fmtBigUsdt(w.quoteNotional)}</span>
                <span className="w-12 text-right num text-[10px] text-muted-foreground">{w.distanceFromMidPercent.toFixed(2)}%</span>
              </div>
            </button>
          )
        })}
        {groups.length > PAGE_SIZE && (
          <div className="flex items-center justify-between pt-1 text-[11px] text-muted-foreground">
            <span>
              {safePage * PAGE_SIZE + 1}–{Math.min((safePage + 1) * PAGE_SIZE, groups.length)} of {groups.length}
            </span>
            <div className="flex items-center gap-1">
              <Button size="sm" variant="outline" className="h-6 px-2" disabled={safePage === 0} onClick={() => setPage(p => Math.max(0, p - 1))}>
                Prev
              </Button>
              <span className="px-1 font-mono">{safePage + 1}/{totalPages}</span>
              <Button size="sm" variant="outline" className="h-6 px-2" disabled={safePage >= totalPages - 1} onClick={() => setPage(p => Math.min(totalPages - 1, p + 1))}>
                Next
              </Button>
            </div>
          </div>
        )}
      </CardContent>
      {selectedWall && <WallDetailModal wall={selectedWall} onClose={() => setSelectedWall(null)} />}
    </Card>
  )
}

function FilterRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="flex items-center justify-between gap-2">
      <span className="text-[10px] uppercase tracking-wider text-muted-foreground">{label}</span>
      <div className="flex items-center gap-2">{children}</div>
    </label>
  )
}

function fmtBigUsdt(n: number): string {
  if (!n || !isFinite(n)) return '0'
  if (n >= 1e9) return `${(n / 1e9).toFixed(2)}B`
  if (n >= 1e6) return `${(n / 1e6).toFixed(2)}M`
  if (n >= 1e3) return `${(n / 1e3).toFixed(2)}K`
  return n.toFixed(0)
}

function fmtPrice(n: number): string {
  if (n >= 1000) return n.toLocaleString('en-US', { maximumFractionDigits: 2 })
  if (n >= 1) return n.toFixed(4)
  return n.toPrecision(4)
}
