import { useEffect, useState } from 'react'
import { Trash2 } from 'lucide-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AuthGuard } from '@/components/auth-guard'
import { NavBar } from '@/components/nav-bar'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { api, ApiError, type StrategyDto } from '@/lib/api'
import { qk } from '@/lib/queries'

type ParamDoc = {
  key: string
  type: 'number' | 'integer' | 'string' | 'enum'
  default: string | number
  desc: string
  options?: string[]   // for enum
}
type StrategyDoc = {
  summary: string
  params: ParamDoc[]
  note?: string
}

// Single source of truth: param metadata. We render the JSON template AND the help panel
// from this so the two never drift out of sync.
const STRATEGY_DOCS: Record<string, StrategyDoc> = {
  sma_cross: {
    summary: 'Hai đường SMA nhanh/chậm — BUY khi SMA fast cắt SMA slow lên.',
    params: [
      { key: 'fast', type: 'integer', default: 9, desc: 'Chu kỳ SMA nhanh (số nến).' },
      { key: 'slow', type: 'integer', default: 21, desc: 'Chu kỳ SMA chậm (số nến). Phải > fast.' },
    ],
  },
  rsi: {
    summary: 'RSI mean-reversion — BUY khi vượt oversold từ dưới lên, SELL khi cắt overbought xuống.',
    params: [
      { key: 'period', type: 'integer', default: 14, desc: 'Chu kỳ RSI.' },
      { key: 'oversold', type: 'number', default: 30, desc: 'Mức quá bán — RSI vượt lên qua đây thì BUY.' },
      { key: 'overbought', type: 'number', default: 70, desc: 'Mức quá mua — RSI cắt xuống qua đây thì SELL.' },
    ],
  },
  breakout: {
    summary: 'Breakout — BUY khi close phá đỉnh lookback (có biên đệm).',
    params: [
      { key: 'lookback', type: 'integer', default: 20, desc: 'Số nến tham chiếu tính high/low.' },
      { key: 'buffer', type: 'number', default: 0.001, desc: 'Biên đệm. 0.001 = 0.1% — close phải vượt high × (1+buffer).' },
    ],
  },
  volume_spike: {
    summary: 'Volume đột biến so với trung bình N nến trước — kết hợp lọc thanh khoản 24h.',
    params: [
      { key: 'multiplier', type: 'number', default: 5, desc: 'Volume hiện tại phải gấp N lần trung bình lookback nến trước.' },
      { key: 'lookback', type: 'integer', default: 20, desc: 'Số nến đóng gần nhất dùng làm baseline (cho phép 5–50).' },
      { key: 'minVolume24h', type: 'number', default: 500000, desc: 'Vol/24h tối thiểu (USDT) — bỏ qua coin rác.' },
      { key: 'direction', type: 'enum', default: 'buy', options: ['buy', 'sell', 'both'], desc: 'Chiều spike chấp nhận.' },
    ],
  },
  vwap_emotion_cross: {
    summary: 'VWAP = "lý trí", MA20/28 = "cảm xúc". Entry khi lý trí đi ngang + cảm xúc đảo chiều + close cắt MA.',
    params: [
      { key: 'maPeriod', type: 'enum', default: 20, options: ['20', '28'], desc: 'MA cảm xúc — chọn 20 hoặc 28.' },
      { key: 'vwapAnchor', type: 'enum', default: 'daily', options: ['daily', 'weekly', 'monthly'], desc: 'VWAP lý trí neo theo phiên. daily/weekly → interval 1h; monthly → interval 2h.' },
      { key: 'vwapFlatThresholdPct', type: 'number', default: 0.05, desc: '|Δ VWAP / price| dưới mức này (%/bar) coi là "lý trí đi ngang". Số càng nhỏ → ít signal nhưng chắc hơn.' },
      { key: 'direction', type: 'enum', default: 'both', options: ['buy', 'sell', 'both'], desc: 'Chiều entry chấp nhận.' },
    ],
    note: 'Metadata kèm tín hiệu: `vwapAboveMa` (true = lý trí > cảm xúc → setup bền) · `maDistanceFromVwapPct` lớn = đang pump quá đà.',
  },
  sentiment_momentum: {
    summary: 'Rolling sentiment score từ tin tức scraped + manual — vượt threshold thì BUY.',
    params: [
      { key: 'threshold', type: 'number', default: 0.5, desc: 'BUY khi điểm sentiment rolling vượt mức này.' },
      { key: 'minSampleCount', type: 'integer', default: 3, desc: 'Cần ít nhất N tin gần đây mới kích hoạt.' },
    ],
  },
  composite: {
    summary: 'Gộp nhiều strategy con — bot chỉ vào lệnh khi tất cả (hoặc N/M) cùng phát tín hiệu.',
    params: [
      { key: 'logic', type: 'enum', default: 'all', options: ['all', 'any', 'quorum'], desc: '"all" = mọi child phải đồng thuận · "any" = chỉ cần 1 · "quorum" = cần minMatch child trong số N child cùng tín hiệu.' },
      { key: 'minMatch', type: 'integer', default: 0, desc: 'Chỉ dùng khi logic="quorum". Mặc định 0 → tự tính = đa số (N/2+1). Set thủ công nếu muốn ngưỡng khác.' },
      { key: 'directionMustMatch', type: 'enum', default: 'true', options: ['true', 'false'], desc: 'true = mọi tín hiệu phải cùng chiều (buy/sell), bất đồng = bỏ qua. false = bỏ phiếu theo đa số, hoà = bỏ qua.' },
      { key: 'children', type: 'string', default: '[ ... ]', desc: 'Mảng strategy con. Mỗi item: { "kind": "<kind>", "params": { ... } }. KHÔNG được dùng "composite" làm child (no nesting).' },
    ],
    note: 'Ví dụ thực dụng: VWAP flat (lý trí) + MA cross (cảm xúc) + Volume spike (xác nhận lực mua) — chỉ vào khi cả 3 đồng thuận. Score đầu ra = trung bình score của các child contribute. WarmupBars = max của các child.',
  },
}

function defaultsJsonFor(kind: string): string {
  // Composite needs a real nested example because the doc's `default` for `children` can't
  // express the full structure in a single line.
  if (kind === 'composite') {
    return `{
  "logic": "all",
  "minMatch": 0,
  "directionMustMatch": true,
  "children": [
    {
      "kind": "vwap_emotion_cross",
      "params": {
        "maPeriod": 20,
        "vwapAnchor": "daily",
        "vwapFlatThresholdPct": 0.05,
        "direction": "both"
      }
    },
    {
      "kind": "sma_cross",
      "params": { "fast": 9, "slow": 21 }
    },
    {
      "kind": "volume_spike",
      "params": { "multiplier": 3, "lookback": 20, "minVolume24h": 500000, "direction": "buy" }
    }
  ]
}`
  }
  const doc = STRATEGY_DOCS[kind]
  if (!doc) return '{}'
  const body = doc.params
    .map(p => `  ${JSON.stringify(p.key)}: ${JSON.stringify(p.default)}`)
    .join(',\n')
  return `{\n${body}\n}`
}

export default function StrategiesPage() {
  return <AuthGuard><Inner /></AuthGuard>
}

function Inner() {
  const qc = useQueryClient()
  const [name, setName] = useState('')
  const [kind, setKind] = useState('sma_cross')
  const [params, setParams] = useState(() => defaultsJsonFor('sma_cross'))
  const [err, setErr] = useState<string | null>(null)

  const { data: list = [] } = useQuery({
    queryKey: qk.strategies,
    queryFn: () => api<StrategyDto[]>('/api/strategies'),
  })
  const { data: kinds = [] } = useQuery({
    queryKey: ['strategy-kinds'],
    queryFn: () => api<string[]>('/api/strategies/kinds'),
    staleTime: Infinity,
  })

  useEffect(() => { setParams(defaultsJsonFor(kind)) }, [kind])

  const createMut = useMutation({
    mutationFn: (body: { name: string; kind: string; parametersJson: string }) =>
      api('/api/strategies', { method: 'POST', body: JSON.stringify(body) }),
    onSuccess: () => {
      setName('')
      qc.invalidateQueries({ queryKey: qk.strategies })
    },
    onError: (e) => setErr(e instanceof ApiError ? e.message : (e as Error).message),
  })

  const removeMut = useMutation({
    mutationFn: (id: string) => api(`/api/strategies/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: qk.strategies }),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    setErr(null)
    try {
      // Backend parser accepts // comments + trailing commas; strip them here so this
      // pre-validate matches BE behaviour and templates with inline help still pass.
      JSON.parse(stripJsonComments(params))
    } catch (e) {
      setErr('Invalid JSON: ' + (e as Error).message)
      return
    }
    createMut.mutate({ name, kind, parametersJson: params })
  }

  function remove(id: string) {
    if (!confirm('Delete strategy?')) return
    removeMut.mutate(id)
  }

  return (
    <main className="min-h-screen">
      <NavBar />
      <div className="container grid gap-5 py-5 lg:grid-cols-[1fr_380px]">
        <Card>
          <CardHeader><CardTitle>Strategies</CardTitle></CardHeader>
          <CardContent>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead><TableHead>Kind</TableHead><TableHead>Params</TableHead><TableHead></TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {list.map(s => (
                  <TableRow key={s.id}>
                    <TableCell className="font-medium">{s.name}</TableCell>
                    <TableCell>{s.kind}</TableCell>
                    <TableCell className="font-mono text-xs">{s.parametersJson}</TableCell>
                    <TableCell><Button size="sm" variant="ghost" onClick={() => remove(s.id)}><Trash2 className="h-4 w-4 text-destructive" /></Button></TableCell>
                  </TableRow>
                ))}
                {list.length === 0 && <TableRow><TableCell colSpan={4} className="text-muted-foreground">No strategies yet.</TableCell></TableRow>}
              </TableBody>
            </Table>
          </CardContent>
        </Card>

        <Card>
          <CardHeader><CardTitle>Create strategy</CardTitle></CardHeader>
          <CardContent>
            <form onSubmit={submit} className="space-y-3">
              <Field label="Name"><input className="w-full rounded-md border px-3 py-2 text-sm" value={name} onChange={e => setName(e.target.value)} required /></Field>
              <Field label="Kind">
                <select className="w-full rounded-sm border border-border bg-surface px-3 py-2 text-sm text-foreground" value={kind} onChange={e => setKind(e.target.value)}>
                  {kinds.map(k => <option key={k} value={k}>{k}</option>)}
                </select>
              </Field>
              <Field label="Parameters (JSON)">
                <textarea
                  className="h-32 w-full rounded-md border border-border bg-surface px-3 py-2 font-mono text-xs outline-none focus:border-primary"
                  value={params}
                  onChange={e => setParams(e.target.value)}
                  spellCheck={false}
                />
              </Field>

              <ParamGuide doc={STRATEGY_DOCS[kind]} onResetDefaults={() => setParams(defaultsJsonFor(kind))} />

              {err && <p className="text-sm text-destructive">{err}</p>}
              <Button type="submit" className="w-full" disabled={createMut.isPending}>{createMut.isPending ? 'Saving...' : 'Create'}</Button>
            </form>
          </CardContent>
        </Card>
      </div>
    </main>
  )
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return <label className="block"><span className="text-sm text-muted-foreground">{label}</span><div className="mt-1">{children}</div></label>
}

function ParamGuide({ doc, onResetDefaults }: { doc: StrategyDoc | undefined; onResetDefaults: () => void }) {
  if (!doc) return null
  return (
    <div className="rounded-md border border-border/60 bg-surface-2/30 text-xs">
      <div className="flex items-start justify-between gap-2 border-b border-border/40 px-3 py-2">
        <div className="space-y-0.5">
          <p className="text-[10px] uppercase tracking-wider text-muted-foreground">Parameter guide</p>
          <p className="text-[11px] leading-snug text-foreground/90">{doc.summary}</p>
        </div>
        <button
          type="button"
          onClick={onResetDefaults}
          className="shrink-0 rounded-sm border border-border/60 px-2 py-1 text-[10px] uppercase tracking-wider text-muted-foreground hover:border-primary/40 hover:text-foreground"
          title="Khôi phục JSON về giá trị mặc định"
        >
          Reset
        </button>
      </div>
      <ul className="divide-y divide-border/30">
        {doc.params.map(p => (
          <li key={p.key} className="grid grid-cols-[140px_1fr] gap-3 px-3 py-2">
            <div className="space-y-1">
              <code className="font-mono text-[11px] text-primary">{p.key}</code>
              <div className="flex flex-wrap items-center gap-1">
                <span className="rounded-sm bg-surface-2 px-1.5 py-0.5 font-mono text-[9px] uppercase tracking-wider text-muted-foreground">
                  {p.type}
                </span>
                <span className="rounded-sm border border-border/60 px-1.5 py-0.5 font-mono text-[9px] text-muted-foreground">
                  default {JSON.stringify(p.default)}
                </span>
              </div>
            </div>
            <div className="space-y-1 text-[11px] leading-snug text-foreground/85">
              <p>{p.desc}</p>
              {p.options && (
                <p className="text-[10px] text-muted-foreground">
                  Cho phép:{' '}
                  {p.options.map((o, i) => (
                    <span key={o}>
                      <code className="rounded-sm bg-surface-2 px-1 font-mono">{o}</code>
                      {i < p.options!.length - 1 ? ' · ' : ''}
                    </span>
                  ))}
                </p>
              )}
            </div>
          </li>
        ))}
      </ul>
      {doc.note && (
        <p className="border-t border-border/40 px-3 py-2 text-[10px] leading-snug text-muted-foreground">
          {doc.note}
        </p>
      )}
    </div>
  )
}

// Mirrors the backend's JsonDocumentOptions { CommentHandling = Skip, AllowTrailingCommas = true }.
// We strip `// line` and `/* block */` comments outside strings, then drop trailing commas.
function stripJsonComments(src: string): string {
  let out = ''
  let i = 0
  let inStr = false
  let strCh = ''
  while (i < src.length) {
    const c = src[i], n = src[i + 1]
    if (inStr) {
      out += c
      if (c === '\\' && i + 1 < src.length) { out += src[i + 1]; i += 2; continue }
      if (c === strCh) inStr = false
      i++; continue
    }
    if (c === '"' || c === "'") { inStr = true; strCh = c; out += c; i++; continue }
    if (c === '/' && n === '/') { while (i < src.length && src[i] !== '\n') i++; continue }
    if (c === '/' && n === '*') { i += 2; while (i < src.length && !(src[i] === '*' && src[i + 1] === '/')) i++; i += 2; continue }
    out += c; i++
  }
  return out.replace(/,(\s*[}\]])/g, '$1')
}
