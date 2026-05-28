import { useMemo, useState } from 'react'
import { Brain, Filter, RefreshCw } from 'lucide-react'
import { useQuery } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { api } from '@/lib/api'

/**
 * Widget that replaced "Momentum Watchlist". Surfaces top-100 USDT pairs where right now:
 *   - the anchored VWAP is sideways (rational stays still)
 *   - the MA20/28 just reversed direction (emotion flips)
 *   - close just crossed the MA on the latest bar
 *
 * Same rule as the `vwap_emotion_cross` strategy bot uses — so anything you see here is a
 * candidate that strategy could also pick up if you wire a bot to that symbol/interval.
 */
type VwapCrossHit = {
  symbol: string
  side: 'buy' | 'sell'
  price: number
  ma: number
  vwap: number
  vwapSlopePct: number
  maDistanceFromVwapPct: number
  vwapAboveMa: boolean
  crossedAt: string
  score: number
  conditions: { vwapFlat: boolean; maReversed: boolean; crossed: boolean }
  missing: string[]
}
type ScanResponse = {
  updatedAt: string
  filter: { anchor: string; maPeriod: number; interval: string; vwapFlatThresholdPct: number; direction: string }
  scanned: number
  count: number
  nearCount: number
  results: VwapCrossHit[]
  nearMatches: VwapCrossHit[]
}

const ANCHOR_INTERVAL: Record<string, string> = { daily: '1h', weekly: '1h', monthly: '2h' }
const MISSING_LABEL: Record<string, string> = {
  vwap_flat: 'VWAP',
  ma_reversal: 'MA',
  close_cross: 'cross',
}

export function VwapCrossScanner() {
  const [anchor, setAnchor] = useState<'daily' | 'weekly' | 'monthly'>('monthly')
  const [maPeriod, setMaPeriod] = useState(20)
  const [direction, setDirection] = useState<'buy' | 'sell' | 'both'>('both')
  const [flatPct, setFlatPct] = useState('0.1')
  const [open, setOpen] = useState(false)
  const [tab, setTab] = useState<'hits' | 'near'>('hits')
  // Interval auto-paired with anchor — most users won't override.
  const interval = ANCHOR_INTERVAL[anchor] ?? '1h'

  const params = useMemo(() => ({
    anchor, maPeriod: String(maPeriod), interval,
    vwapFlatThresholdPct: flatPct, direction,
  }), [anchor, maPeriod, interval, flatPct, direction])

  const { data, isFetching, refetch, error } = useQuery({
    queryKey: ['market', 'vwap-cross', params],
    queryFn: () => {
      const q = new URLSearchParams(params)
      return api<ScanResponse>(`/api/market/vwap-cross-scan?${q.toString()}`)
    },
    staleTime: 30_000,
    refetchInterval: 60_000,
  })

  const hits = data?.results ?? []
  const near = data?.nearMatches ?? []
  const rows = tab === 'hits' ? hits : near

  return (
    <Card>
      <CardHeader className="flex flex-row items-center justify-between gap-2 pb-2">
        <div>
          <CardTitle className="flex items-center gap-2 text-sm">
            <Brain className="h-4 w-4 text-primary" /> VWAP Cross Scanner
          </CardTitle>
          <p className="text-[11px] text-muted-foreground">
            {data
              ? `${data.count} hits · ${data.scanned} scanned · ${data.filter.anchor} VWAP / MA${data.filter.maPeriod} on ${data.filter.interval}`
              : 'Scanning…'}
          </p>
        </div>
        <div className="flex items-center gap-1">
          <Button size="sm" variant="outline" className="h-7 w-7 p-0" onClick={() => setOpen(v => !v)} title="Filters">
            <Filter className="h-3.5 w-3.5" />
          </Button>
          <Button size="sm" variant="outline" className="h-7 w-7 p-0" onClick={() => void refetch()} disabled={isFetching} title="Refresh">
            <RefreshCw className={`h-3.5 w-3.5 ${isFetching ? 'animate-spin' : ''}`} />
          </Button>
        </div>
      </CardHeader>

      {open && (
        <CardContent className="space-y-2 border-b border-border/40 px-3 pb-3 pt-0">
          <div className="grid grid-cols-2 gap-2 text-xs">
            <Field label="VWAP anchor">
              <select value={anchor} onChange={e => setAnchor(e.target.value as typeof anchor)}
                className="h-7 w-full rounded-sm border border-border bg-surface px-2 text-xs">
                <option value="daily">daily (1h)</option>
                <option value="weekly">weekly (1h)</option>
                <option value="monthly">monthly (2h)</option>
              </select>
            </Field>
            <Field label="MA period">
              <select value={maPeriod} onChange={e => setMaPeriod(Number(e.target.value))}
                className="h-7 w-full rounded-sm border border-border bg-surface px-2 text-xs">
                <option value={20}>20</option>
                <option value={28}>28</option>
              </select>
            </Field>
            <Field label="VWAP flat threshold (%/bar)">
              <input value={flatPct} onChange={e => setFlatPct(e.target.value)}
                className="h-7 w-full rounded-sm border border-border bg-surface px-2 font-mono text-xs" />
            </Field>
            <Field label="Direction">
              <select value={direction} onChange={e => setDirection(e.target.value as typeof direction)}
                className="h-7 w-full rounded-sm border border-border bg-surface px-2 text-xs">
                <option value="both">Both</option>
                <option value="buy">Buy only</option>
                <option value="sell">Sell only</option>
              </select>
            </Field>
          </div>
        </CardContent>
      )}

      <CardContent className="px-3 pb-3 pt-2">
        <div className="mb-2 flex items-center gap-1 border-b border-border/40">
          <TabBtn active={tab === 'hits'} onClick={() => setTab('hits')}>Hits {data ? `(${hits.length})` : ''}</TabBtn>
          <TabBtn active={tab === 'near'} onClick={() => setTab('near')}>Near 2/3 {data ? `(${near.length})` : ''}</TabBtn>
        </div>

        {error && <p className="text-xs text-destructive">{(error as Error).message}</p>}
        {!error && data && rows.length === 0 && (
          <p className="text-[11px] text-muted-foreground">
            {tab === 'hits'
              ? `No full hits. Try the Near 2/3 tab to see coin sắp setup, loosen VWAP flat (>${flatPct}%) hoặc đợi nến đóng tiếp.`
              : 'No near matches either — market is genuinely quiet right now.'}
          </p>
        )}
        {rows.length > 0 && (() => {
          // Shared scale so bars are visually comparable: widest divergence in the visible set,
          // floored at 8% so a calm market doesn't blow tiny moves up to full-width.
          const scaleMax = Math.max(8, ...rows.map(r => Math.abs(r.maDistanceFromVwapPct)))
          return (
          <div className="space-y-1">
            <div className="grid grid-cols-[96px_1fr_46px_62px_64px_64px_76px] items-center gap-2 px-1 text-[10px] uppercase tracking-wider text-muted-foreground">
              <span>Symbol</span>
              <span className="text-center">MA ◄ VWAP ► (pump)</span>
              <span className="text-right">Side</span><span className="text-right">Price</span><span className="text-right">MA-VWAP</span><span className="text-right">Quality</span>
              <span className="text-right">{tab === 'hits' ? 'Cross at' : 'Missing'}</span>
            </div>
            {rows.map(h => (
              <a
                key={`${h.symbol}-${h.side}`}
                href={`https://www.tradingview.com/chart/?symbol=BINANCE:${h.symbol}&interval=${tvInterval(interval)}`}
                target="_blank" rel="noopener noreferrer"
                className="grid grid-cols-[96px_1fr_46px_62px_64px_64px_76px] items-center gap-2 rounded-sm border border-border/40 bg-surface px-2 py-1.5 text-[11px] hover:border-primary/40 hover:bg-surface-2"
                title={`MA${data?.filter.maPeriod} ${h.ma} · VWAP ${h.vwap} · slope ${h.vwapSlopePct}%`}
              >
                <span className="truncate font-mono font-medium">{h.symbol}</span>
                <DivergenceBar pct={h.maDistanceFromVwapPct} max={scaleMax} />
                <span className={`text-right font-mono uppercase ${h.side === 'buy' ? 'text-up' : 'text-down'}`}>{h.side}</span>
                <span className="text-right num">{fmtPrice(h.price)}</span>
                <span className={`text-right num ${h.maDistanceFromVwapPct >= 0 ? 'text-down' : 'text-up'}`}>
                  {h.maDistanceFromVwapPct >= 0 ? '+' : ''}{h.maDistanceFromVwapPct.toFixed(2)}%
                </span>
                <span className={`text-right text-[10px] ${h.vwapAboveMa ? 'text-up' : 'text-warning'}`}>
                  {h.vwapAboveMa ? '✓ bền' : '⚠ pump'}
                </span>
                <span className="text-right text-[10px]">
                  {tab === 'hits'
                    ? new Date(h.crossedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
                    : h.missing.map(m => MISSING_LABEL[m] ?? m).join(' + ')}
                </span>
              </a>
            ))}
          </div>
          )
        })()}
      </CardContent>
    </Card>
  )
}

/**
 * Horizontal divergence bar: how far MA sits from the anchored VWAP, signed.
 * Center = 0 (MA on VWAP). Bar grows left when MA is BELOW VWAP (green, "bền" — rational still
 * above price action) and right when MA is ABOVE VWAP (red, "pump" — price stretched over VWAP).
 * Width is proportional to |%| against a shared scale so rows are comparable at a glance.
 */
function DivergenceBar({ pct, max }: { pct: number; max: number }) {
  const positive = pct >= 0
  const frac = Math.min(1, Math.abs(pct) / max)
  const halfWidth = frac * 50 // % of the track, each side spans up to 50%
  const color = positive ? 'hsl(var(--destructive))' : 'hsl(var(--success))'
  return (
    <div
      className="relative h-3.5 w-full overflow-hidden rounded-sm bg-muted/15"
      title={`MA is ${Math.abs(pct).toFixed(2)}% ${positive ? 'above' : 'below'} VWAP`}
    >
      {/* zero center line */}
      <div className="absolute left-1/2 top-0 h-full w-px -translate-x-1/2 bg-border/70" />
      {/* magnitude bar from center */}
      <div
        className="absolute top-1/2 h-2 -translate-y-1/2 rounded-sm"
        style={
          positive
            ? { left: '50%', width: `${halfWidth}%`, backgroundColor: color }
            : { right: '50%', width: `${halfWidth}%`, backgroundColor: color }
        }
      />
    </div>
  )
}

function TabBtn({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`-mb-px border-b-2 px-3 py-1.5 text-xs transition-colors ${
        active ? 'border-primary text-primary' : 'border-transparent text-muted-foreground hover:text-foreground'
      }`}
    >
      {children}
    </button>
  )
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block space-y-0.5">
      <span className="text-[10px] uppercase tracking-wider text-muted-foreground">{label}</span>
      {children}
    </label>
  )
}

function fmtPrice(n: number): string {
  if (n >= 1000) return n.toLocaleString('en-US', { maximumFractionDigits: 2 })
  if (n >= 1) return n.toFixed(4)
  return n.toPrecision(4)
}

// TradingView interval codes — auto-match what we asked Binance for so user sees same bar.
function tvInterval(iv: string): string {
  return iv === '15m' ? '15' : iv === '30m' ? '30' : iv === '1h' ? '60' : iv === '2h' ? '120' : iv === '4h' ? '240' : '60'
}
