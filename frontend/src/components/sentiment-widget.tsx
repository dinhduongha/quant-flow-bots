import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Brain, Send, ShieldAlert, Trash2 } from 'lucide-react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { api, ApiError, type SentimentEventDto, type SentimentSnapshotDto } from '@/lib/api'
import { qk } from '@/lib/queries'

/**
 * Compact "Sentiment & Risk" panel. Risk-blocked symbols (delist/hack/suspend) are extreme
 * negative sentiment, so they live here as the top section instead of a separate card. Below:
 * top bullish/bearish (rolling EWMA), recent headlines, and a manual ingest form (keyword scorer
 * runs server-side). Real-time: SignalRQueryBridge invalidates `['sentiment', …]` on push.
 */

type RiskFlag = { symbol: string; reason: string; source: string; url: string | null; at: string }

const REASON_LABEL: Record<string, string> = {
  delisting_announced: 'Delist announced',
  trading_suspension: 'Trading suspended',
  security_incident: 'Hack / exploit',
  binance_status_not_trading: 'Status: not trading',
  binance_alert: 'Binance alert',
}

// Examples hit known keywords in KeywordSentimentScorer so users get a non-zero score
// when they click — they immediately see the bull/bear classification in action.
const EXAMPLES: { symbol: string; headline: string; tone: 'up' | 'down' }[] = [
  { symbol: 'BTCUSDT', headline: 'Bitcoin ETF approved by SEC',  tone: 'up' },
  { symbol: 'SOLUSDT', headline: 'Solana hits new all-time high', tone: 'up' },
  { symbol: 'ETHUSDT', headline: 'Major exchange hack reported',  tone: 'down' },
  { symbol: 'BNBUSDT', headline: 'Token delisted from Binance',   tone: 'down' },
]

export function SentimentWidget() {
  const qc = useQueryClient()
  const { data: bull = [] } = useQuery({
    queryKey: qk.sentimentTop(5, 'bull'),
    queryFn: () => api<SentimentSnapshotDto[]>('/api/sentiment/top?n=5&direction=bull'),
    refetchInterval: 30_000,
  })
  const { data: bear = [] } = useQuery({
    queryKey: qk.sentimentTop(5, 'bear'),
    queryFn: () => api<SentimentSnapshotDto[]>('/api/sentiment/top?n=5&direction=bear'),
    refetchInterval: 30_000,
  })
  const { data: recent = [] } = useQuery({
    queryKey: qk.sentimentRecent,
    queryFn: () => api<SentimentEventDto[]>('/api/sentiment/recent?limit=15'),
    refetchInterval: 30_000,
  })
  const { data: riskFlags = [] } = useQuery({
    queryKey: ['market', 'risk-flags'],
    queryFn: () => api<RiskFlag[]>('/api/market/risk-flags'),
    refetchInterval: 60_000,
  })

  const [symbol, setSymbol] = useState('BTCUSDT')
  const [headline, setHeadline] = useState('')
  const [err, setErr] = useState<string | null>(null)

  const ingest = useMutation({
    mutationFn: (body: unknown) => api('/api/sentiment/manual', { method: 'POST', body: JSON.stringify(body) }),
    onSuccess: () => { setHeadline(''); setErr(null); qc.invalidateQueries({ queryKey: ['sentiment'] }) },
    onError: (e) => setErr(e instanceof ApiError ? e.message : (e as Error).message),
  })

  const removeOne = useMutation({
    mutationFn: (id: string) => api(`/api/sentiment/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['sentiment'] }),
  })

  const removeAll = useMutation({
    mutationFn: () => api('/api/sentiment/all', { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['sentiment'] }),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!headline.trim() || !symbol.trim()) return
    ingest.mutate({ symbolCode: symbol.toUpperCase(), headline })
  }

  const hasTops = bull.length > 0 || bear.length > 0
  const [showAddForm, setShowAddForm] = useState(false)

  // Each Binance delist announcement covers N symbols, but we emit N rows (one per symbol)
  // so the per-symbol EWMA can update. UI dedupes back to one row per announcement so the
  // recent feed stays readable. Group key = url when present, else headline+timestamp bucket.
  const groups = useMemo(() => {
    const map = new Map<string, EventGroupData>()
    for (const s of recent) {
      const key = s.url ?? `${s.headline}::${s.at.slice(0, 16)}`
      const g = map.get(key)
      if (g) { g.symbols.push(s.symbolCode); g.ids.push(s.id); g.score = Math.max(Math.abs(g.score), Math.abs(s.score)) * Math.sign(s.score || g.score) }
      else map.set(key, { key, headline: s.headline, at: s.at, url: s.url ?? null, source: s.source, score: s.score, symbols: [s.symbolCode], ids: [s.id] })
    }
    return Array.from(map.values()).sort((a, b) => new Date(b.at).getTime() - new Date(a.at).getTime())
  }, [recent])

  return (
    <Card>
      <CardHeader className="pb-1.5">
        <div className="flex items-center justify-between gap-2">
          <CardTitle className="flex items-center gap-1.5 text-sm">
            <Brain className="h-4 w-4 text-primary" /> Sentiment &amp; Risk
          </CardTitle>
          <div className="flex items-center gap-1.5">
            {riskFlags.length > 0 && (
              <span className="flex items-center gap-1 rounded-sm bg-destructive/15 px-1.5 py-0.5 text-[10px] font-medium text-destructive" title="Risk-blocked symbols">
                <ShieldAlert className="h-3 w-3" /> {riskFlags.length}
              </span>
            )}
            <button
              type="button"
              onClick={() => setShowAddForm(v => !v)}
              className="rounded-sm border border-border/60 px-1.5 py-0.5 text-[10px] uppercase tracking-wider text-muted-foreground hover:border-primary/40 hover:text-foreground"
              title="Add manual headline"
            >
              {showAddForm ? '− Hide' : '+ Add'}
            </button>
          </div>
        </div>
        <p className="text-[10px] text-muted-foreground">EWMA per symbol · feeds <code className="font-mono">sentiment_momentum</code></p>
      </CardHeader>
      <CardContent className="space-y-2 px-3 pb-2">
        {riskFlags.length > 0 && <RiskBlockedSection flags={riskFlags} />}

        {hasTops && (
          <div className="grid grid-cols-2 gap-2 text-xs">
            {bull.length > 0 && <Column title="Bullish" items={bull} accent="text-up" />}
            {bear.length > 0 && <Column title="Bearish" items={bear} accent="text-down" />}
          </div>
        )}

        {showAddForm && (
          <div className="space-y-1.5 rounded-sm border border-border/40 bg-surface/30 p-2">
            <form onSubmit={submit} className="flex gap-1">
              <input
                className="h-6 w-20 rounded-sm border border-border bg-surface px-1.5 font-mono text-[11px] uppercase outline-none focus:border-primary"
                value={symbol}
                onChange={e => setSymbol(e.target.value)}
                placeholder="SYMBOL"
                maxLength={20}
              />
              <input
                className="h-6 flex-1 rounded-sm border border-border bg-surface px-1.5 text-[11px] outline-none focus:border-primary"
                value={headline}
                onChange={e => setHeadline(e.target.value)}
                placeholder="Headline (free text)"
              />
              <Button type="submit" size="sm" className="h-6 px-1.5" disabled={ingest.isPending}>
                <Send className="h-3 w-3" />
              </Button>
            </form>
            {/* Examples as 2x2 grid — tighter than flex-wrap when card width is narrow. */}
            <div className="grid grid-cols-2 gap-1 text-[10px]">
              {EXAMPLES.map(ex => (
                <button
                  key={ex.headline}
                  type="button"
                  onClick={() => { setSymbol(ex.symbol); setHeadline(ex.headline); setErr(null) }}
                  className={`truncate rounded-sm border border-border/60 px-1.5 py-0.5 text-left hover:border-primary/50 ${ex.tone === 'up' ? 'text-up' : 'text-down'}`}
                  title={`Fill: ${ex.symbol} · ${ex.headline}`}
                >
                  {ex.tone === 'up' ? '▲' : '▼'} {ex.headline}
                </button>
              ))}
            </div>
            {err && <p className="text-[10px] text-destructive">{err}</p>}
          </div>
        )}

        <div className="border-t border-border/40 pt-2">
          {groups.length > 0 && (
            <div className="flex items-center justify-between pb-1.5 text-[9px] uppercase tracking-wider text-muted-foreground">
              <span>Recent · {groups.length}</span>
              <button
                type="button"
                onClick={() => { if (confirm('Xoá toàn bộ sentiment events (cả manual + scraped)?')) removeAll.mutate() }}
                disabled={removeAll.isPending}
                className="rounded-sm px-1 py-0.5 hover:text-destructive"
              >
                Clear all
              </button>
            </div>
          )}
          <div className="space-y-1.5">
            {groups.slice(0, 8).map(g => (
              <EventGroup key={g.key} group={g}
                onDelete={() => g.ids.forEach(id => removeOne.mutate(id))} />
            ))}
          </div>
          {groups.length === 0 && <p className="text-[10px] text-muted-foreground">No sentiment events yet.</p>}
        </div>
      </CardContent>
    </Card>
  )
}

/** Risk-blocked symbols — the extreme-negative end of sentiment. Bots won't open new trades here. */
function RiskBlockedSection({ flags }: { flags: RiskFlag[] }) {
  return (
    <div className="space-y-1.5 rounded-sm border border-destructive/40 bg-destructive/5 p-2">
      <div className="flex items-center justify-between gap-2">
        <span className="flex items-center gap-1.5 text-[11px] font-medium uppercase tracking-wider text-destructive">
          <ShieldAlert className="h-3.5 w-3.5" /> Risk-blocked
        </span>
        <span className="text-[10px] text-muted-foreground">{flags.length}</span>
      </div>
      <p className="text-[10px] leading-snug text-muted-foreground">
        Bot sẽ KHÔNG mở lệnh mới trên các symbol này. Position cũ đã được auto-close.
      </p>
      <ul className="space-y-1">
        {flags.slice(0, 8).map(f => (
          <li key={f.symbol + f.at} className="flex items-center justify-between gap-2 rounded-sm border border-destructive/30 bg-destructive/10 px-2 py-1 text-[11px]">
            <span className="font-mono font-medium">{f.symbol}</span>
            <span className="flex-1 truncate text-[10px] text-muted-foreground" title={f.reason}>
              {REASON_LABEL[f.reason] ?? f.reason}
            </span>
            {f.url ? (
              <a href={f.url} target="_blank" rel="noopener noreferrer"
                 className="text-[10px] text-primary hover:underline" title="Open source">↗</a>
            ) : null}
          </li>
        ))}
      </ul>
    </div>
  )
}

type EventGroupData = {
  key: string
  headline: string
  at: string
  url: string | null
  source: string
  score: number
  symbols: string[]
  ids: string[]
}

function EventGroup({ group, onDelete }: { group: EventGroupData; onDelete: () => void }) {
  // Detect Binance scraped events vs manual entries — different visual treatment.
  const isBinance = group.source === 'binance_announcement'
  const isDelist = isBinance && /delist/i.test(group.headline)
  // Strip Binance's redundant date suffix so the headline fits in one line.
  const cleanHeadline = group.headline.replace(/\s*on\s+\d{4}-\d{2}-\d{2}.*$/i, '').replace(/^Binance Will\s+/i, '').trim()
  const toneRing = isDelist ? 'border-destructive/40 bg-destructive/5' : group.score > 0.1 ? 'border-up/30 bg-up/5' : group.score < -0.1 ? 'border-down/30 bg-down/5' : 'border-border/40 bg-surface/40'

  return (
    <div className={`group/row rounded-sm border ${toneRing} px-2 py-1.5`}>
      <div className="flex items-center gap-1.5">
        {isDelist && <span className="shrink-0 text-[10px]" title="Delisting">🛑</span>}
        <span className="flex-1 truncate text-[11px] font-medium" title={group.headline}>{cleanHeadline}</span>
        <span className="shrink-0 text-[9px] text-muted-foreground">{relTime(group.at)}</span>
        <button
          type="button"
          onClick={onDelete}
          className="shrink-0 rounded-sm p-0.5 text-muted-foreground opacity-0 transition hover:text-destructive group-hover/row:opacity-100"
          title={`Delete ${group.symbols.length} event(s)`}
        >
          <Trash2 className="h-2.5 w-2.5" />
        </button>
      </div>
      {group.symbols.length > 0 && group.symbols[0] !== 'MARKET' && (
        <div className="mt-1 flex flex-wrap gap-1">
          {group.symbols.map(s => (
            <span key={s} className={`rounded-sm px-1 py-0.5 font-mono text-[9px] ${isDelist ? 'bg-destructive/15 text-destructive' : 'bg-surface-2 text-muted-foreground'}`}>
              {s}
            </span>
          ))}
        </div>
      )}
      {group.url && (
        <div className="mt-1 flex items-center justify-between text-[9px]">
          <a href={group.url} target="_blank" rel="noopener noreferrer" className="text-primary hover:underline">
            View on Telegram ↗
          </a>
          <span className="text-muted-foreground">{group.source}</span>
        </div>
      )}
    </div>
  )
}

function Column({ title, items, accent }: { title: string; items: SentimentSnapshotDto[]; accent: string }) {
  return (
    <div>
      <div className="mb-0.5 text-[9px] uppercase tracking-wider text-muted-foreground">{title}</div>
      <div className="space-y-0.5">
        {items.slice(0, 4).map(s => (
          <div key={s.symbolCode} className="flex items-center justify-between gap-1 rounded-sm bg-surface/40 px-1.5 py-0.5">
            <span className="truncate font-mono text-[10px]">{s.symbolCode}</span>
            <Badge variant="outline" className={`h-3.5 border-0 px-1 text-[9px] font-mono ${accent}`}>{s.rollingScore.toFixed(2)}</Badge>
          </div>
        ))}
      </div>
    </div>
  )
}

function scoreClass(s: number): string {
  if (s > 0.1) return 'w-9 text-right font-mono text-up'
  if (s < -0.1) return 'w-9 text-right font-mono text-down'
  return 'w-9 text-right font-mono text-muted-foreground'
}

function relTime(iso: string): string {
  const dt = new Date(iso)
  const diff = (Date.now() - dt.getTime()) / 1000
  if (diff < 60) return `${Math.round(diff)}s`
  if (diff < 3600) return `${Math.round(diff / 60)}m`
  if (diff < 86400) return `${Math.round(diff / 3600)}h`
  return `${Math.round(diff / 86400)}d`
}
