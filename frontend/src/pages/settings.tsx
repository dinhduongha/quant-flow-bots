import { useEffect, useMemo, useState } from 'react'
import { KeyRound, Power, PowerOff, ShieldCheck, Trash2 } from 'lucide-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AuthGuard } from '@/components/auth-guard'
import { NavBar } from '@/components/nav-bar'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Bell, Send, Activity, Layers } from 'lucide-react'
import { api, ApiError, type ApiKeyDto, type ExchangeDto, type UserSettingsDto } from '@/lib/api'
import { qk } from '@/lib/queries'

export default function SettingsPage() {
  return <AuthGuard><Inner /></AuthGuard>
}

function Inner() {
  const qc = useQueryClient()
  const [label, setLabel] = useState('Binance main')
  const [exchangeCode, setExchangeCode] = useState('binance')
  const [mode, setMode] = useState('Paper')
  const [apiKeyValue, setApiKeyValue] = useState('')
  const [apiSecret, setApiSecret] = useState('')
  const [permissionsJson, setPermissionsJson] = useState('{"spot":true,"trade":false,"withdraw":false}')
  const [err, setErr] = useState<string | null>(null)

  const { data: exchanges = [], error: exchangesErr } = useQuery({
    queryKey: qk.exchanges,
    queryFn: () => api<ExchangeDto[]>('/api/settings/exchanges'),
    staleTime: 5 * 60_000,
  })
  const { data: keys = [], isFetching: reloading, refetch: refetchKeys } = useQuery({
    queryKey: qk.apiKeys,
    queryFn: () => api<ApiKeyDto[]>('/api/settings/api-keys'),
  })
  const reloadErr = exchangesErr ? (exchangesErr as Error).message : null
  const activeLiveKey = useMemo(() => keys.find(k => k.mode === 'Live' && k.isActive), [keys])

  // Auto-pick first exchange code once loaded.
  useEffect(() => {
    if (exchanges[0] && exchangeCode === 'binance' && !exchanges.find(e => e.code === 'binance')) {
      setExchangeCode(exchanges[0].code)
    }
  }, [exchanges, exchangeCode])

  useEffect(() => {
    if (exchangeCode === 'binance-futures-testnet') {
      setMode('Live')
      setPermissionsJson('{"futures":true,"trade":true,"withdraw":false}')
    }
  }, [exchangeCode])

  const invalidateKeys = () => qc.invalidateQueries({ queryKey: qk.apiKeys })

  const submitMut = useMutation({
    mutationFn: (body: unknown) => api<ApiKeyDto>('/api/settings/api-keys', { method: 'POST', body: JSON.stringify(body) }),
    onSuccess: () => { setApiKeyValue(''); setApiSecret(''); invalidateKeys() },
    onError: (e) => setErr(e instanceof ApiError ? e.message : (e as Error).message),
  })
  const toggleMut = useMutation({
    mutationFn: (row: ApiKeyDto) =>
      api<ApiKeyDto>(`/api/settings/api-keys/${row.id}/${row.isActive ? 'deactivate' : 'activate'}`, { method: 'POST' }),
    onSuccess: invalidateKeys,
  })
  const removeMut = useMutation({
    mutationFn: (id: string) => api(`/api/settings/api-keys/${id}`, { method: 'DELETE' }),
    onSuccess: invalidateKeys,
  })
  const validateMut = useMutation({
    mutationFn: (id: string) =>
      api<{ validatedAt: string; canTrade: boolean; canWithdraw: boolean; totalWalletBalance: number; availableBalance: number }>(
        `/api/settings/api-keys/${id}/validate`, { method: 'POST' }),
    onSuccess: (res) => { setErr(null); alert(`Validated. canTrade=${res.canTrade} canWithdraw=${res.canWithdraw} balance=${res.availableBalance}`); invalidateKeys() },
    onError: (e) => setErr(e instanceof ApiError ? e.message : (e as Error).message),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    setErr(null)
    try { JSON.parse(permissionsJson) }
    catch (e) { setErr('Invalid JSON: ' + (e as Error).message); return }
    submitMut.mutate({
      exchangeCode,
      label,
      apiKey: apiKeyValue,
      apiSecret,
      mode,
      isActive: true,
      permissionsJson,
    })
  }
  const reload = () => { void refetchKeys() }
  const toggle = (row: ApiKeyDto) => toggleMut.mutate(row)
  const remove = (id: string) => { if (confirm('Delete API key?')) removeMut.mutate(id) }
  const busy = submitMut.isPending

  return (
    <main className="min-h-screen">
      <NavBar />
      <div className="container grid gap-5 py-5 lg:grid-cols-[1fr_400px]">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between">
            <CardTitle>API keys</CardTitle>
            <Button size="sm" variant="outline" onClick={() => void reload()} disabled={reloading}>
              {reloading ? 'Reloading...' : 'Reload'}
            </Button>
          </CardHeader>
          <CardContent className="space-y-4">
            {reloadErr && (
              <div className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
                Reload failed: {reloadErr}. API might be restarting — try again in a moment.
              </div>
            )}
            {activeLiveKey && (
              <div className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
                Active live key: {activeLiveKey.label} ({activeLiveKey.keyPreview})
              </div>
            )}
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Label</TableHead>
                  <TableHead>Exchange</TableHead>
                  <TableHead>Mode</TableHead>
                  <TableHead>Key</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead></TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {keys.map(k => (
                  <TableRow key={k.id}>
                    <TableCell className="font-medium">{k.label}</TableCell>
                    <TableCell>{k.exchangeCode}</TableCell>
                    <TableCell><Badge variant={k.mode === 'Live' ? 'default' : 'outline'}>{k.mode}</Badge></TableCell>
                    <TableCell className="font-mono text-xs">{k.keyPreview}</TableCell>
                    <TableCell>
                      <Badge variant={k.isActive ? 'default' : 'outline'}>{k.isActive ? 'Active' : 'Inactive'}</Badge>
                    </TableCell>
                    <TableCell className="flex justify-end gap-1">
                      {k.exchangeCode === 'binance-futures-testnet' && (
                        <Button size="sm" variant="outline" title="Validate against Binance Futures testnet" disabled={validateMut.isPending} onClick={() => validateMut.mutate(k.id)}>
                          <ShieldCheck className="h-3.5 w-3.5" />
                        </Button>
                      )}
                      <Button size="sm" variant="outline" onClick={() => toggle(k)}>
                        {k.isActive ? <PowerOff className="h-3.5 w-3.5" /> : <Power className="h-3.5 w-3.5" />}
                      </Button>
                      <Button size="sm" variant="ghost" onClick={() => remove(k.id)}>
                        <Trash2 className="h-4 w-4 text-destructive" />
                      </Button>
                    </TableCell>
                  </TableRow>
                ))}
                {keys.length === 0 && (
                  <TableRow>
                    <TableCell colSpan={6} className="text-muted-foreground">No API keys configured.</TableCell>
                  </TableRow>
                )}
              </TableBody>
            </Table>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2"><KeyRound className="h-5 w-5" /> Add API key</CardTitle>
          </CardHeader>
          <CardContent>
            <form onSubmit={submit} className="space-y-3">
              <Field label="Exchange">
                <select className="w-full rounded-sm border border-border bg-surface px-3 py-2 text-sm text-foreground" value={exchangeCode} onChange={e => setExchangeCode(e.target.value)}>
                  {exchanges.map(e => <option key={e.id} value={e.code}>{e.name}</option>)}
                </select>
              </Field>
              <Field label="Label">
                <input className="w-full rounded-md border px-3 py-2 text-sm" value={label} onChange={e => setLabel(e.target.value)} required />
              </Field>
              <Field label="Mode">
                <select className="w-full rounded-sm border border-border bg-surface px-3 py-2 text-sm text-foreground" value={mode} onChange={e => setMode(e.target.value)}>
                  <option value="Paper">Paper</option>
                  <option value="Live">Live</option>
                </select>
              </Field>
              <Field label="API key">
                <input className="w-full rounded-md border px-3 py-2 font-mono text-sm" value={apiKeyValue} onChange={e => setApiKeyValue(e.target.value)} autoComplete="off" required />
              </Field>
              <Field label="API secret">
                <input className="w-full rounded-md border px-3 py-2 font-mono text-sm" type="password" value={apiSecret} onChange={e => setApiSecret(e.target.value)} autoComplete="new-password" required />
              </Field>
              <Field label="Permissions JSON">
                <textarea className="h-24 w-full rounded-md border px-3 py-2 font-mono text-xs" value={permissionsJson} onChange={e => setPermissionsJson(e.target.value)} />
              </Field>
              {err && <p className="text-sm text-destructive">{err}</p>}
              <Button type="submit" className="w-full" disabled={busy}>{busy ? 'Saving...' : 'Save key'}</Button>
            </form>
          </CardContent>
        </Card>

        <NotificationsSection />
      </div>
    </main>
  )
}

// Single card with internal tabs — replaces the two stacked cards. Keeps each tab's form
// state independent (their own useState), so switching tabs doesn't drop in-progress edits.
function NotificationsSection() {
  const [tab, setTab] = useState<'bot' | 'whale' | 'wall'>('bot')
  const { data: settings } = useQuery({
    queryKey: qk.userSettings,
    queryFn: () => api<UserSettingsDto>('/api/me/settings'),
  })

  return (
    <Card className="lg:col-span-2">
      <div className="flex items-center justify-between border-b border-border/40 px-4 pt-3">
        <div className="flex gap-1">
          <TabButton active={tab === 'bot'} onClick={() => setTab('bot')}
            icon={<Bell className="h-3.5 w-3.5" />}
            label="Bot alerts"
            status={settings?.telegramAlertsEnabled ? 'on' : settings?.telegramBotTokenConfigured ? 'configured' : 'off'} />
          <TabButton active={tab === 'whale'} onClick={() => setTab('whale')}
            icon={<Activity className="h-3.5 w-3.5" />}
            label="Whale alerts"
            status={settings?.whaleAlertEnabled ? 'on' : settings?.whaleAlertBotTokenConfigured ? 'configured' : 'off'} />
          <TabButton active={tab === 'wall'} onClick={() => setTab('wall')}
            icon={<Layers className="h-3.5 w-3.5" />}
            label="Wall alerts"
            status={settings?.wallAlertEnabled ? 'on' : settings?.wallAlertBotTokenConfigured ? 'configured' : 'off'} />
        </div>
        <span className="text-[10px] text-muted-foreground">Telegram notifications</span>
      </div>
      <CardContent className="px-4 py-4">
        {tab === 'bot' && <TelegramTabBody settings={settings} />}
        {tab === 'whale' && <WhaleAlertTabBody settings={settings} />}
        {tab === 'wall' && <WallAlertTabBody settings={settings} />}
      </CardContent>
    </Card>
  )
}

function TabButton({ active, onClick, icon, label, status }:
  { active: boolean; onClick: () => void; icon: React.ReactNode; label: string; status: 'on' | 'configured' | 'off' }) {
  const dotCls = status === 'on' ? 'bg-up' : status === 'configured' ? 'bg-warning' : 'bg-muted-foreground/40'
  return (
    <button
      type="button"
      onClick={onClick}
      className={`-mb-px flex items-center gap-2 border-b-2 px-3 py-2 text-xs transition ${
        active ? 'border-primary text-foreground' : 'border-transparent text-muted-foreground hover:text-foreground'
      }`}
    >
      {icon}
      <span className="font-medium">{label}</span>
      <span className={`h-1.5 w-1.5 rounded-full ${dotCls}`} title={status} />
    </button>
  )
}

function TelegramTabBody({ settings }: { settings: UserSettingsDto | undefined }) {
  const qc = useQueryClient()
  const [token, setToken] = useState('')
  const [chatId, setChatId] = useState('')
  const [enabled, setEnabled] = useState(false)
  const [msg, setMsg] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)

  // Sync local form state from server payload (don't override while user is typing token).
  useEffect(() => {
    if (settings) {
      setEnabled(settings.telegramAlertsEnabled)
      setChatId(settings.telegramChatId ?? '')
    }
  }, [settings])

  const saveMut = useMutation({
    mutationFn: (body: Record<string, unknown>) =>
      api<UserSettingsDto>('/api/me/settings', { method: 'PUT', body: JSON.stringify(body) }),
    onMutate: () => setMsg(null),
    onSuccess: () => { setToken(''); setMsg({ kind: 'ok', text: 'Saved.' }); qc.invalidateQueries({ queryKey: qk.userSettings }) },
    onError: (e) => setMsg({ kind: 'err', text: e instanceof ApiError ? e.message : (e as Error).message }),
  })
  const testMut = useMutation({
    mutationFn: () => api('/api/me/settings/telegram/test', { method: 'POST' }),
    onMutate: () => setMsg(null),
    onSuccess: () => setMsg({ kind: 'ok', text: 'Test message sent — check Telegram.' }),
    onError: (e) => setMsg({ kind: 'err', text: e instanceof ApiError ? e.message : (e as Error).message }),
  })

  function save(e: React.FormEvent) {
    e.preventDefault()
    const body: Record<string, unknown> = {
      telegramAlertsEnabled: enabled,
      telegramChatId: chatId.trim(),
    }
    if (token.trim()) body.telegramBotToken = token.trim()
    saveMut.mutate(body)
  }
  const test = () => testMut.mutate()
  const saving = saveMut.isPending
  const testing = testMut.isPending

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-1.5 text-[10px]">
        <span className="uppercase tracking-wider text-muted-foreground">Forwarded events:</span>
        <EventChip>⚠ risk</EventChip>
        <EventChip>🛑 auto_close</EventChip>
        <EventChip>🎯 SL / TP hit</EventChip>
        <EventChip>📌 break-even</EventChip>
        <EventChip>🚫 blocked orders</EventChip>
        <a href="https://core.telegram.org/bots#how-do-i-create-a-bot" target="_blank" rel="noopener"
           className="ml-auto text-[10px] text-primary hover:underline">How to create a bot ↗</a>
      </div>
      <form onSubmit={save} className="grid gap-3 md:grid-cols-[1fr_240px]">
          <div className="space-y-3">
            <Field label="Bot token">
              <input
                className="h-9 w-full rounded-sm border border-border bg-surface px-3 font-mono text-sm"
                type="password"
                placeholder={settings?.telegramBotTokenConfigured ? '••••••••• (configured, leave blank to keep)' : '123456:ABC-DEF...'}
                value={token}
                onChange={e => setToken(e.target.value)}
                autoComplete="off"
              />
            </Field>
            <Field label="Chat ID">
              <input
                className="h-9 w-full rounded-sm border border-border bg-surface px-3 font-mono text-sm"
                placeholder="e.g. 123456789 or -100123…"
                value={chatId}
                onChange={e => setChatId(e.target.value)}
                autoComplete="off"
              />
            </Field>
            <label className="flex items-center gap-2 rounded-sm border border-border bg-surface px-2.5 py-2 text-xs">
              <input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)} className="accent-primary" />
              <span>Enable Telegram alerts</span>
            </label>
            {msg && (
              <p className={`text-xs ${msg.kind === 'err' ? 'text-destructive' : 'text-up'}`}>{msg.text}</p>
            )}
          </div>
          <div className="flex flex-col gap-2">
            <Button type="submit" className="w-full" disabled={saving}>{saving ? 'Saving…' : 'Save'}</Button>
            <Button
              type="button"
              variant="outline"
              className="w-full gap-2"
              onClick={test}
              disabled={testing || !settings?.telegramBotTokenConfigured || !chatId}
            >
              <Send className="h-3.5 w-3.5" /> {testing ? 'Sending…' : 'Send test'}
            </Button>
            <div className="rounded-sm border border-border/40 bg-surface px-2.5 py-2 text-[11px] text-muted-foreground">
              <p>Status: {settings?.telegramBotTokenConfigured ? <span className="text-up">Token configured</span> : <span className="text-warning">No token</span>}</p>
              <p>Alerts: {settings?.telegramAlertsEnabled ? <span className="text-up">On</span> : <span>Off</span>}</p>
            </div>
          </div>
        </form>
      </div>
  )
}

function EventChip({ children }: { children: React.ReactNode }) {
  return <span className="rounded-sm border border-border/60 bg-surface px-1.5 py-0.5 font-mono text-muted-foreground">{children}</span>
}

const WHALE_INTERVALS = ['5m', '15m', '30m', '1h', '2h', '4h'] as const

function WhaleAlertTabBody({ settings }: { settings: UserSettingsDto | undefined }) {
  const qc = useQueryClient()
  const [token, setToken] = useState('')
  const [chatId, setChatId] = useState('')
  const [enabled, setEnabled] = useState(false)
  const [intervals, setIntervals] = useState<Set<string>>(new Set(['15m']))
  const [multiplier, setMultiplier] = useState('5')
  const [minVol, setMinVol] = useState('500000')
  const [mode, setMode] = useState<'intrabar' | 'candle_close'>('candle_close')
  const [cooldown, setCooldown] = useState('60')
  const [direction, setDirection] = useState<'buy' | 'sell' | 'both'>('both')
  const [lookback, setLookback] = useState('20')
  const [msg, setMsg] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)

  useEffect(() => {
    if (!settings) return
    setEnabled(settings.whaleAlertEnabled)
    setChatId(settings.whaleAlertChatId ?? '')
    setIntervals(new Set((settings.whaleAlertIntervals ?? '15m').split(',').filter(Boolean)))
    setMultiplier(String(settings.whaleAlertMultiplier ?? 5))
    setMinVol(String(settings.whaleAlertMinVolume24h ?? 500000))
    setMode(settings.whaleAlertMode ?? 'candle_close')
    setCooldown(String(settings.whaleAlertCooldownMinutes ?? 60))
    setDirection((settings.whaleAlertDirection || 'both') as 'buy' | 'sell' | 'both')
    setLookback(String(settings.whaleAlertLookback ?? 20))
  }, [settings])

  const saveMut = useMutation({
    mutationFn: (body: Record<string, unknown>) =>
      api<UserSettingsDto>('/api/me/settings', { method: 'PUT', body: JSON.stringify(body) }),
    onMutate: () => setMsg(null),
    onSuccess: () => { setToken(''); setMsg({ kind: 'ok', text: 'Saved.' }); qc.invalidateQueries({ queryKey: qk.userSettings }) },
    onError: (e) => setMsg({ kind: 'err', text: e instanceof ApiError ? e.message : (e as Error).message }),
  })
  const testMut = useMutation({
    mutationFn: () => api('/api/me/settings/whale-alerts/test', { method: 'POST' }),
    onMutate: () => setMsg(null),
    onSuccess: () => setMsg({ kind: 'ok', text: 'Test message sent — check Telegram.' }),
    onError: (e) => setMsg({ kind: 'err', text: e instanceof ApiError ? e.message : (e as Error).message }),
  })

  function toggleInterval(iv: string) {
    setIntervals(s => {
      const next = new Set(s)
      if (next.has(iv)) next.delete(iv); else next.add(iv)
      return next
    })
  }
  function save(e: React.FormEvent) {
    e.preventDefault()
    if (intervals.size === 0) { setMsg({ kind: 'err', text: 'Pick at least 1 interval.' }); return }
    const body: Record<string, unknown> = {
      whaleAlertEnabled: enabled,
      whaleAlertChatId: chatId.trim(),
      whaleAlertIntervals: Array.from(intervals).join(','),
      whaleAlertMultiplier: Number(multiplier),
      whaleAlertMinVolume24h: Number(minVol),
      whaleAlertMode: mode,
      whaleAlertCooldownMinutes: Number(cooldown),
      whaleAlertDirection: direction,
      whaleAlertLookback: Number(lookback),
    }
    if (token.trim()) body.whaleAlertBotToken = token.trim()
    saveMut.mutate(body)
  }
  const saving = saveMut.isPending
  const testing = testMut.isPending

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-1.5 text-[10px]">
        <span className="uppercase tracking-wider text-muted-foreground">Trigger:</span>
        <EventChip>📊 candle volume ≥ N× baseline</EventChip>
        <EventChip>🌐 all USDT pairs ≥ min vol</EventChip>
        <EventChip>🤖 separate Telegram bot</EventChip>
      </div>
      <form onSubmit={save} className="grid gap-3 md:grid-cols-[1fr_240px]">
          <div className="space-y-3">
            <div className="grid gap-3 md:grid-cols-2">
              <Field label="Bot token (separate from above)">
                <input
                  className="h-9 w-full rounded-sm border border-border bg-surface px-3 font-mono text-sm"
                  type="password"
                  placeholder={settings?.whaleAlertBotTokenConfigured ? '••••••••• (configured)' : '123456:ABC-DEF...'}
                  value={token}
                  onChange={e => setToken(e.target.value)}
                  autoComplete="off"
                />
              </Field>
              <Field label="Chat ID">
                <input
                  className="h-9 w-full rounded-sm border border-border bg-surface px-3 font-mono text-sm"
                  value={chatId}
                  onChange={e => setChatId(e.target.value)}
                  autoComplete="off"
                />
              </Field>
            </div>

            <Field label="Intervals to watch (multi-select)">
              <div className="flex flex-wrap gap-1.5">
                {WHALE_INTERVALS.map(iv => (
                  <button
                    key={iv} type="button"
                    onClick={() => toggleInterval(iv)}
                    className={`h-7 rounded-sm border px-2 text-xs font-mono ${intervals.has(iv) ? 'border-primary bg-primary/20 text-primary' : 'border-border bg-surface text-muted-foreground'}`}
                  >{iv}</button>
                ))}
              </div>
            </Field>

            <div className="grid gap-3 md:grid-cols-4">
              <Field label="Multiplier (×)">
                <input
                  type="number" min={2} max={50} step={0.5}
                  className="h-9 w-full rounded-sm border border-border bg-surface px-3 font-mono text-sm"
                  value={multiplier} onChange={e => setMultiplier(e.target.value)}
                />
              </Field>
              <Field label="Lookback (candles)">
                <input
                  type="number" min={5} max={50}
                  className="h-9 w-full rounded-sm border border-border bg-surface px-3 font-mono text-sm"
                  value={lookback} onChange={e => setLookback(e.target.value)}
                  title="Number of previous candles averaged as baseline. Current candle volume is compared to this average."
                />
              </Field>
              <Field label="Min 24h vol (USDT)">
                <input
                  type="number" min={0} step={100000}
                  className="h-9 w-full rounded-sm border border-border bg-surface px-3 font-mono text-sm"
                  value={minVol} onChange={e => setMinVol(e.target.value)}
                />
              </Field>
              <Field label="Cooldown (minutes)">
                <input
                  type="number" min={5} max={1440}
                  className="h-9 w-full rounded-sm border border-border bg-surface px-3 font-mono text-sm"
                  value={cooldown} onChange={e => setCooldown(e.target.value)}
                />
              </Field>
            </div>

            <div className="grid gap-3 md:grid-cols-2">
              <Field label="Detection mode">
                <select
                  className="h-9 w-full rounded-sm border border-border bg-surface px-3 text-sm"
                  value={mode} onChange={e => setMode(e.target.value as 'intrabar' | 'candle_close')}
                >
                  <option value="candle_close">candle_close — wait for candle close (steadier)</option>
                  <option value="intrabar">intrabar — fire on open candle (faster)</option>
                </select>
              </Field>
              <Field label="Direction">
                <select
                  className="h-9 w-full rounded-sm border border-border bg-surface px-3 text-sm"
                  value={direction} onChange={e => setDirection(e.target.value as 'buy' | 'sell' | 'both')}
                >
                  <option value="both">Both buy + sell</option>
                  <option value="buy">Buy only</option>
                  <option value="sell">Sell only</option>
                </select>
              </Field>
            </div>

            <label className="flex items-center gap-2 rounded-sm border border-border bg-surface px-2.5 py-2 text-xs">
              <input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)} className="accent-primary" />
              <span>Enable Whale Alerts</span>
            </label>
            {msg && <p className={`text-xs ${msg.kind === 'err' ? 'text-destructive' : 'text-up'}`}>{msg.text}</p>}
          </div>

          <div className="flex flex-col gap-2">
            <Button type="submit" className="w-full" disabled={saving}>{saving ? 'Saving…' : 'Save'}</Button>
            <Button
              type="button" variant="outline" className="w-full gap-2"
              onClick={() => testMut.mutate()}
              disabled={testing || !settings?.whaleAlertBotTokenConfigured || !chatId}
            >
              <Send className="h-3.5 w-3.5" /> {testing ? 'Sending…' : 'Send test'}
            </Button>
            <div className="rounded-sm border border-border/40 bg-surface px-2.5 py-2 text-[11px] text-muted-foreground">
              <p>Status: {settings?.whaleAlertBotTokenConfigured ? <span className="text-up">Token configured</span> : <span className="text-warning">No token</span>}</p>
              <p>Alerts: {settings?.whaleAlertEnabled ? <span className="text-up">On</span> : <span>Off</span>}</p>
              <p>Watching: {Array.from(intervals).join(', ') || '—'}</p>
            </div>
          </div>
        </form>
      </div>
  )
}

function WallAlertTabBody({ settings }: { settings: UserSettingsDto | undefined }) {
  const qc = useQueryClient()
  const [token, setToken] = useState('')
  const [chatId, setChatId] = useState('')
  const [enabled, setEnabled] = useState(false)
  const [minNotional, setMinNotional] = useState('500000')
  const [maxDistancePct, setMaxDistancePct] = useState('2')
  const [side, setSide] = useState<'' | 'Bid' | 'Ask'>('')
  const [cooldown, setCooldown] = useState('30')
  const [msg, setMsg] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)

  useEffect(() => {
    if (!settings) return
    setEnabled(settings.wallAlertEnabled)
    setChatId(settings.wallAlertChatId ?? '')
    setMinNotional(String(settings.wallAlertMinNotional ?? 500000))
    setMaxDistancePct(String(settings.wallAlertMaxDistancePct ?? 2))
    setSide(settings.wallAlertSide ?? '')
    setCooldown(String(settings.wallAlertCooldownMinutes ?? 30))
  }, [settings])

  const saveMut = useMutation({
    mutationFn: (body: Record<string, unknown>) =>
      api<UserSettingsDto>('/api/me/settings', { method: 'PUT', body: JSON.stringify(body) }),
    onMutate: () => setMsg(null),
    onSuccess: () => { setToken(''); setMsg({ kind: 'ok', text: 'Saved.' }); qc.invalidateQueries({ queryKey: qk.userSettings }) },
    onError: (e) => setMsg({ kind: 'err', text: e instanceof ApiError ? e.message : (e as Error).message }),
  })
  const testMut = useMutation({
    mutationFn: () => api('/api/me/settings/wall-alerts/test', { method: 'POST' }),
    onMutate: () => setMsg(null),
    onSuccess: () => setMsg({ kind: 'ok', text: 'Test message sent — check Telegram.' }),
    onError: (e) => setMsg({ kind: 'err', text: e instanceof ApiError ? e.message : (e as Error).message }),
  })

  function save(e: React.FormEvent) {
    e.preventDefault()
    const body: Record<string, unknown> = {
      wallAlertEnabled: enabled,
      wallAlertChatId: chatId.trim(),
      wallAlertMinNotional: Number(minNotional),
      wallAlertMaxDistancePct: Number(maxDistancePct),
      wallAlertSide: side,
      wallAlertCooldownMinutes: Number(cooldown),
    }
    if (token.trim()) body.wallAlertBotToken = token.trim()
    saveMut.mutate(body)
  }

  const saving = saveMut.isPending
  const testing = testMut.isPending

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-1.5 text-[10px]">
        <span className="uppercase tracking-wider text-muted-foreground">Trigger:</span>
        <EventChip>🧱 single price level ≥ MinNotional</EventChip>
        <EventChip>📏 within MaxDistance % of mid</EventChip>
        <EventChip>🤖 separate Telegram bot</EventChip>
      </div>
      <form onSubmit={save} className="grid gap-3 md:grid-cols-[1fr_240px]">
        <div className="space-y-3">
          <div className="grid gap-3 md:grid-cols-2">
            <Field label="Bot token (separate from above)">
              <input
                className="h-9 w-full rounded-sm border border-border bg-surface px-3 font-mono text-sm"
                type="password"
                placeholder={settings?.wallAlertBotTokenConfigured ? '••••••••• (configured)' : '123456:ABC-DEF...'}
                value={token}
                onChange={e => setToken(e.target.value)}
                autoComplete="off"
              />
            </Field>
            <Field label="Chat ID">
              <input
                className="h-9 w-full rounded-sm border border-border bg-surface px-3 font-mono text-sm"
                value={chatId}
                onChange={e => setChatId(e.target.value)}
                autoComplete="off"
              />
            </Field>
          </div>

          <div className="grid gap-3 md:grid-cols-4">
            <Field label="Min notional (USDT)">
              <input
                type="number" min={0} step={100000}
                className="h-9 w-full rounded-sm border border-border bg-surface px-3 font-mono text-sm"
                value={minNotional} onChange={e => setMinNotional(e.target.value)}
              />
            </Field>
            <Field label="Max distance % from mid">
              <input
                type="number" min={0} max={20} step={0.1}
                className="h-9 w-full rounded-sm border border-border bg-surface px-3 font-mono text-sm"
                value={maxDistancePct} onChange={e => setMaxDistancePct(e.target.value)}
              />
            </Field>
            <Field label="Side">
              <select
                className="h-9 w-full rounded-sm border border-border bg-surface px-3 text-sm"
                value={side} onChange={e => setSide(e.target.value as '' | 'Bid' | 'Ask')}
              >
                <option value="">Both</option>
                <option value="Bid">Bid (buy wall)</option>
                <option value="Ask">Ask (sell wall)</option>
              </select>
            </Field>
            <Field label="Cooldown (minutes)">
              <input
                type="number" min={1} max={1440}
                className="h-9 w-full rounded-sm border border-border bg-surface px-3 font-mono text-sm"
                value={cooldown} onChange={e => setCooldown(e.target.value)}
                title="Per-symbol-per-side dedupe window. Same wall re-detection inside this window is suppressed."
              />
            </Field>
          </div>

          <label className="flex items-center gap-2 rounded-sm border border-border bg-surface px-2.5 py-2 text-xs">
            <input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)} className="accent-primary" />
            <span>Enable Wall Alerts</span>
          </label>
          {msg && <p className={`text-xs ${msg.kind === 'err' ? 'text-destructive' : 'text-up'}`}>{msg.text}</p>}
        </div>

        <div className="flex flex-col gap-2">
          <Button type="submit" className="w-full" disabled={saving}>{saving ? 'Saving…' : 'Save'}</Button>
          <Button
            type="button" variant="outline" className="w-full gap-2"
            onClick={() => testMut.mutate()}
            disabled={testing || !settings?.wallAlertBotTokenConfigured || !chatId}
          >
            <Send className="h-3.5 w-3.5" /> {testing ? 'Sending…' : 'Send test'}
          </Button>
          <div className="rounded-sm border border-border/40 bg-surface px-2.5 py-2 text-[11px] text-muted-foreground">
            <p>Status: {settings?.wallAlertBotTokenConfigured ? <span className="text-up">Token configured</span> : <span className="text-warning">No token</span>}</p>
            <p>Alerts: {settings?.wallAlertEnabled ? <span className="text-up">On</span> : <span>Off</span>}</p>
            <p>Filter: ≥ ${Number(minNotional).toLocaleString()} · |Δ| ≤ {maxDistancePct}% · {side || 'both'}</p>
          </div>
        </div>
      </form>
    </div>
  )
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return <label className="block"><span className="text-sm text-muted-foreground">{label}</span><div className="mt-1">{children}</div></label>
}
