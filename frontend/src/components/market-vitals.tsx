import { useQuery } from '@tanstack/react-query'
import { Card, CardContent } from '@/components/ui/card'
import { api } from '@/lib/api'

/**
 * "Market Vitals" — two compact radial gauges side by side: crypto Fear & Greed (left) and our
 * Binance REST weight usage (right). Replaces the two separate stacked widgets so the dashboard
 * right rail is shorter and reads at a glance. Both are reference glances, not focal panels.
 */
type FearGreedResp = {
  value: number
  classification: string
  updatedAt: string
  nextUpdateInSeconds: number | null
  history: { value: number; at: string }[]
}

type RateLimitResp = {
  usedWeight: number
  limit: number
  usedPercent: number
  level: 'normal' | 'slowDown' | 'criticalOnly' | 'banned'
  windowResetInSeconds: number
  gate: { isOpen: boolean; until: string | null; statusCode: number | null; reason: string | null; openCount24h: number }
}

const FG_ZONES = [
  { max: 24, label: 'Extreme Fear', color: '#dc2626' },
  { max: 49, label: 'Fear', color: '#f97316' },
  { max: 54, label: 'Neutral', color: '#eab308' },
  { max: 74, label: 'Greed', color: '#84cc16' },
  { max: 100, label: 'Extreme Greed', color: '#16a34a' },
]
const fgZone = (v: number) => FG_ZONES.find(z => v <= z.max) ?? FG_ZONES[4]

const WEIGHT_LEVELS = {
  normal: { label: 'Normal', color: '#16a34a' },
  slowDown: { label: 'Slow-down', color: '#eab308' },
  criticalOnly: { label: 'Critical', color: '#f97316' },
  banned: { label: 'BANNED', color: '#dc2626' },
} as const

export function MarketVitals() {
  const { data: fg } = useQuery({
    queryKey: ['market', 'fear-greed'],
    queryFn: () => api<FearGreedResp>('/api/market/fear-greed'),
    staleTime: 30 * 60_000,
    refetchInterval: 60 * 60_000,
  })
  const { data: rl } = useQuery({
    queryKey: ['market', 'rate-limit'],
    queryFn: () => api<RateLimitResp>('/api/market/rate-limit'),
    refetchInterval: 5_000,
    staleTime: 4_000,
  })

  const zone = fg ? fgZone(fg.value) : null
  const trendFirst = fg?.history?.[0]?.value
  const trendDelta = fg && trendFirst != null ? fg.value - trendFirst : 0

  const lvl = rl ? WEIGHT_LEVELS[rl.level] : null

  return (
    <Card>
      <CardContent className="px-3 py-3">
        <div className="mb-2 text-[10px] uppercase tracking-wider text-muted-foreground">Market Vitals</div>
        <div className="grid grid-cols-2 gap-2">
          {/* Fear & Greed */}
          <Gauge
            pct={fg?.value ?? 0}
            color={zone?.color ?? '#3f3f46'}
            center={fg ? String(fg.value) : '–'}
            title="Fear & Greed"
            subtitle={fg ? zone!.label : 'loading…'}
            subtitleColor={zone?.color}
            footer={
              fg
                ? trendFirst != null
                  ? `${trendFirst} ${trendDelta > 0 ? '▲' : trendDelta < 0 ? '▼' : '·'} ${fg.value}`
                  : undefined
                : undefined
            }
            footerExtra={fg?.nextUpdateInSeconds ? `⟳ ${fmtCountdown(fg.nextUpdateInSeconds)}` : undefined}
          />

          {/* API Weight */}
          <Gauge
            pct={rl?.usedPercent ?? 0}
            color={rl?.gate.isOpen ? '#dc2626' : lvl?.color ?? '#3f3f46'}
            center={rl ? `${rl.usedPercent}%` : '–'}
            title="API Weight"
            subtitle={rl ? (rl.gate.isOpen ? 'BANNED' : lvl!.label) : 'loading…'}
            subtitleColor={rl?.gate.isOpen ? '#dc2626' : lvl?.color}
            footer={rl ? `${rl.usedWeight.toLocaleString()}/${rl.limit.toLocaleString()}` : undefined}
            footerExtra={
              rl
                ? rl.gate.isOpen
                  ? `⛔ ${rl.gate.statusCode}`
                  : `⟳ ${rl.windowResetInSeconds}s`
                : undefined
            }
            divider
          />
        </div>
      </CardContent>
    </Card>
  )
}

function Gauge({
  pct, color, center, title, subtitle, subtitleColor, footer, footerExtra, divider,
}: {
  pct: number
  color: string
  center: string
  title: string
  subtitle: string
  subtitleColor?: string
  footer?: string
  footerExtra?: string
  divider?: boolean
}) {
  const r = 26
  const sw = 6
  const circ = 2 * Math.PI * r
  const p = Math.max(0, Math.min(100, pct))
  const offset = circ * (1 - p / 100)

  return (
    <div className={`flex flex-col items-center gap-1 text-center ${divider ? 'border-l border-border/40 pl-2' : 'pr-2'}`}>
      <div className="relative h-[60px] w-[60px]">
        <svg viewBox="0 0 64 64" className="h-[60px] w-[60px]">
          <circle cx="32" cy="32" r={r} fill="none" strokeWidth={sw} className="stroke-muted/40" />
          <circle
            cx="32" cy="32" r={r} fill="none" strokeWidth={sw} stroke={color} strokeLinecap="round"
            strokeDasharray={circ} strokeDashoffset={offset} transform="rotate(-90 32 32)"
            style={{ transition: 'stroke-dashoffset .6s ease, stroke .3s ease' }}
          />
        </svg>
        <div className="absolute inset-0 flex items-center justify-center">
          <span className="font-mono text-sm font-bold leading-none" style={{ color }}>{center}</span>
        </div>
      </div>
      <div className="leading-tight">
        <div className="text-[10px] font-medium text-muted-foreground">{title}</div>
        <div className="truncate text-[11px] font-semibold" style={{ color: subtitleColor }}>{subtitle}</div>
        {(footer || footerExtra) && (
          <div className="mt-0.5 flex items-center justify-center gap-1.5 font-mono text-[9px] text-muted-foreground">
            {footer && <span>{footer}</span>}
            {footerExtra && <span>{footerExtra}</span>}
          </div>
        )}
      </div>
    </div>
  )
}

function fmtCountdown(sec: number): string {
  if (sec >= 3600) return `${Math.floor(sec / 3600)}h ${Math.floor((sec % 3600) / 60)}m`
  if (sec >= 60) return `${Math.floor(sec / 60)}m`
  return `${sec}s`
}
