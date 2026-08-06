import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { api } from '@/lib/api'
import { useTheme } from '@/lib/theme'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import type { AiUsage } from '@/lib/types'

type FilingStatus = 'Single' | 'MarriedFilingJointly' | 'HeadOfHousehold'

interface Settings {
  finnhubApiKey: string | null
  refreshIntervalSeconds: number
  showClosedPositions: boolean
  theme: string
  filingStatus: FilingStatus
  otherIncome: number
  startWithWindows: boolean
  autoAcceptDiscoveries: boolean
  anthropicApiKey: string | null
  anthropicModel: string
  aiMonthlyTokenBudget: number
  aiWeeklyBatchEnabled: boolean
  aiWeeklyBatchCount: number
  scoreboardDownWeighting: boolean
  aiProvider: 'openwebui' | 'anthropic' | 'disabled'
  openWebUiBaseUrl: string | null
  openWebUiApiKey: string | null
  openWebUiModel: string | null
  openWebUiTimeoutSeconds: number
  openWebUiDisableThinking: boolean
}

interface AccountSummary {
  id: number
  displayName: string
  number: string
  treatment: string
  positions: number
  transactions: number
}

export function SettingsPage() {
  const queryClient = useQueryClient()
  const { setTheme } = useTheme()
  const settings = useQuery({
    queryKey: ['settings'],
    queryFn: () => api.get<Settings>('/api/settings'),
  })
  const accounts = useQuery({
    queryKey: ['settings-accounts'],
    queryFn: () => api.get<AccountSummary[]>('/api/settings/accounts'),
  })
  const aiUsage = useQuery({
    queryKey: ['ai-usage'],
    queryFn: () => api.get<AiUsage>('/api/ai/usage'),
  })

  const [form, setForm] = useState<Settings | null>(null)
  const [deleting, setDeleting] = useState<AccountSummary | null>(null)
  useEffect(() => {
    if (settings.data && form === null) setForm(settings.data)
  }, [settings.data, form])

  const save = useMutation({
    mutationFn: (s: Settings) => api.put('/api/settings', s),
    onSuccess: (_, s) => {
      queryClient.invalidateQueries({ queryKey: ['settings'] })
      queryClient.invalidateQueries({ queryKey: ['dashboard'] })
      queryClient.invalidateQueries({ queryKey: ['tax'] })
      // provider/key changes must reach the brief dialog's enabled state
      // immediately, not after its cache goes stale
      queryClient.invalidateQueries({ queryKey: ['ai-usage'] })
      queryClient.invalidateQueries({ queryKey: ['queue'] })
      setTheme(s.theme === 'light' ? 'light' : 'dark')
      toast.success('Settings saved')
    },
    onError: (e) => toast.error(String(e)),
  })

  const testConnection = useMutation({
    mutationFn: () => api.post<{ ok: boolean; error: string | null }>('/api/settings/test-connection'),
    onSuccess: (r) =>
      r.ok ? toast.success('Finnhub connection works') : toast.error(r.error ?? 'Connection failed'),
    onError: (e) => toast.error(String(e)),
  })

  // tests the values currently in the form (saved or not), so "select
  // provider -> test" works before hitting Save
  const testAi = useMutation({
    mutationFn: () =>
      api.post<{ ok: boolean; error: string | null }>('/api/settings/test-ai', {
        aiProvider: form!.aiProvider,
        openWebUiBaseUrl: form!.openWebUiBaseUrl,
        openWebUiApiKey: form!.openWebUiApiKey,
        openWebUiModel: form!.openWebUiModel,
        openWebUiDisableThinking: form!.openWebUiDisableThinking,
        anthropicApiKey: form!.anthropicApiKey,
        anthropicModel: form!.anthropicModel,
      }),
    onSuccess: (r) =>
      r.ok
        ? toast.success('AI provider responded')
        : toast.error(r.error ?? 'AI connection failed'),
    onError: (e) => toast.error(String(e)),
  })

  const resetPrompts = useMutation({
    mutationFn: () => api.post<{ dir: string }>('/api/settings/prompts/reset'),
    onSuccess: (r) => toast.success(`Prompt templates reset in ${r.dir}`),
    onError: (e) => toast.error(String(e)),
  })

  const deleteAccount = useMutation({
    mutationFn: (id: number) => api.delete(`/api/accounts/${id}`),
    onSuccess: () => {
      queryClient.invalidateQueries()
      toast.success('Account deleted')
    },
    onError: (e) => toast.error(String(e)),
  })

  if (!form) return <div className="p-8 text-sm text-muted-foreground">Loading…</div>

  const set = <K extends keyof Settings>(key: K, value: Settings[K]) =>
    setForm((f) => (f ? { ...f, [key]: value } : f))

  return (
    <div className="mx-auto max-w-3xl space-y-6 p-6">
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Quotes &amp; research</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid grid-cols-[12rem_1fr_auto] items-center gap-3">
            <Label htmlFor="api-key">Finnhub API key</Label>
            <Input
              id="api-key"
              type="password"
              value={form.finnhubApiKey ?? ''}
              onChange={(e) => set('finnhubApiKey', e.target.value || null)}
            />
            <Button
              variant="outline"
              size="sm"
              onClick={() => testConnection.mutate()}
              disabled={testConnection.isPending}
            >
              {testConnection.isPending ? 'Testing…' : 'Test connection'}
            </Button>
            <span />
            <a
              href="https://finnhub.io/register"
              target="_blank"
              rel="noreferrer"
              className="text-xs text-primary hover:underline"
            >
              Get a free API key at finnhub.io
            </a>
            <span />
            <Label htmlFor="interval">Auto-refresh interval (sec)</Label>
            <Input
              id="interval"
              type="number"
              min={10}
              max={3600}
              step={10}
              className="w-32"
              value={form.refreshIntervalSeconds}
              onChange={(e) => set('refreshIntervalSeconds', Number(e.target.value))}
            />
            <span />
          </div>
          <div className="flex items-center gap-2">
            <Checkbox
              id="start-windows"
              checked={form.startWithWindows}
              onCheckedChange={(c) => set('startWithWindows', c === true)}
            />
            <Label htmlFor="start-windows" className="font-normal">
              Start with Windows (minimized to tray)
            </Label>
          </div>
          <div className="flex items-center gap-3">
            <Label>Theme</Label>
            <Select value={form.theme} onValueChange={(v) => set('theme', v)}>
              <SelectTrigger className="w-32">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="dark">Dark</SelectItem>
                <SelectItem value="light">Light</SelectItem>
              </SelectContent>
            </Select>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Research queue &amp; discovery</CardTitle>
        </CardHeader>
        <CardContent className="space-y-3">
          <div className="flex items-center gap-2">
            <Checkbox
              id="auto-accept"
              checked={form.autoAcceptDiscoveries}
              onCheckedChange={(c) => set('autoAcceptDiscoveries', c === true)}
            />
            <Label htmlFor="auto-accept" className="font-normal">
              Auto-accept peer discoveries into a &quot;Discovered&quot; universe
            </Label>
          </div>
          <p className="pl-6 text-xs text-muted-foreground">
            Off: new peers wait in the Discoveries panel for review. On: they are crawled and
            ranked automatically.
          </p>
          <div className="flex items-center gap-2">
            <Checkbox
              id="down-weight"
              checked={form.scoreboardDownWeighting}
              onCheckedChange={(c) => set('scoreboardDownWeighting', c === true)}
            />
            <Label htmlFor="down-weight" className="font-normal">
              Down-weight queue evidence from underperforming signal kinds
            </Label>
          </div>
          <p className="pl-6 text-xs text-muted-foreground">
            Uses the signal scoreboard: kinds whose 60-day sector beat-rate stays under 45% across
            at least 20 matured fires earn half credit in the queue. The affected rows show the
            applied weight and the reason.
          </p>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">AI research briefs</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <p className="text-xs text-muted-foreground">
            Briefs are generated section-by-section strictly from your local data, every number
            grounding-checked against the exact slice the model saw, and badged as AI-generated —
            never as advice. The feature stays off until a provider is configured.
          </p>
          <div className="grid grid-cols-[12rem_1fr] items-center gap-3">
            <Label>Provider</Label>
            <div className="flex items-center gap-2">
              <Select
                value={form.aiProvider}
                onValueChange={(v) => set('aiProvider', v as Settings['aiProvider'])}
              >
                <SelectTrigger className="w-72">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="disabled">Disabled</SelectItem>
                  <SelectItem value="openwebui">Local AI server (OpenWebUI)</SelectItem>
                  <SelectItem value="anthropic">Anthropic API</SelectItem>
                </SelectContent>
              </Select>
              <Button
                variant="outline"
                size="sm"
                onClick={() => testAi.mutate()}
                disabled={testAi.isPending || form.aiProvider === 'disabled'}
              >
                {testAi.isPending ? 'Testing…' : 'Test connection'}
              </Button>
            </div>
          </div>

          {form.aiProvider === 'openwebui' && (
            <div className="grid grid-cols-[12rem_1fr] items-center gap-3">
              <Label htmlFor="ow-url">Server base URL</Label>
              <Input
                id="ow-url"
                placeholder="https://…"
                value={form.openWebUiBaseUrl ?? ''}
                onChange={(e) => set('openWebUiBaseUrl', e.target.value || null)}
              />
              <Label htmlFor="ow-key">API key</Label>
              <Input
                id="ow-key"
                type="password"
                value={form.openWebUiApiKey ?? ''}
                onChange={(e) => set('openWebUiApiKey', e.target.value || null)}
              />
              <Label htmlFor="ow-model">Model</Label>
              <Input
                id="ow-model"
                className="w-72"
                placeholder="model id on the server"
                value={form.openWebUiModel ?? ''}
                onChange={(e) => set('openWebUiModel', e.target.value || null)}
              />
              <Label htmlFor="ow-timeout">Per-call timeout (sec)</Label>
              <Input
                id="ow-timeout"
                type="number"
                min={10}
                max={3600}
                step={30}
                className="w-32"
                value={form.openWebUiTimeoutSeconds}
                onChange={(e) => set('openWebUiTimeoutSeconds', Number(e.target.value))}
              />
              <span />
              <div className="flex items-center gap-2">
                <Checkbox
                  id="ow-no-think"
                  checked={form.openWebUiDisableThinking}
                  onCheckedChange={(c) => set('openWebUiDisableThinking', c === true)}
                />
                <Label htmlFor="ow-no-think" className="font-normal">
                  Skip the model&apos;s &quot;thinking&quot; phase (much faster briefs)
                </Label>
              </div>
              <span />
              <p className="text-xs text-muted-foreground">
                Thinking models (qwen3) deliberate for minutes before each ~10-line section; the
                grounding checker verifies every number regardless, so briefs skip it by default.
                Un-check to let the model think — then raise the per-call timeout, since sections
                can take several minutes each.
              </p>
              <span />
              <p className="text-xs text-muted-foreground">
                Stored locally as OPENWEBUI_BASE_URL / OPENWEBUI_API_KEY / OPENWEBUI_MODEL in the
                git-ignored app settings. Requests run one at a time (shared hardware); each brief
                is five sequential section calls. No token budget. Briefs need network reach to
                the server (VPN).
              </p>
            </div>
          )}

          {form.aiProvider === 'anthropic' && (
            <div className="grid grid-cols-[12rem_1fr] items-center gap-3">
              <Label htmlFor="anthropic-key">Anthropic API key</Label>
              <Input
                id="anthropic-key"
                type="password"
                value={form.anthropicApiKey ?? ''}
                onChange={(e) => set('anthropicApiKey', e.target.value || null)}
              />
              <Label>Model</Label>
              <Select value={form.anthropicModel} onValueChange={(v) => set('anthropicModel', v)}>
                <SelectTrigger className="w-72">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="claude-sonnet-4-6">claude-sonnet-4-6 (default)</SelectItem>
                  <SelectItem value="claude-opus-4-8">claude-opus-4-8</SelectItem>
                  <SelectItem value="claude-haiku-4-5-20251001">claude-haiku-4-5</SelectItem>
                </SelectContent>
              </Select>
              <Label htmlFor="ai-budget">Monthly token budget</Label>
              <div className="flex items-center gap-3">
                <Input
                  id="ai-budget"
                  type="number"
                  min={0}
                  step={50000}
                  className="w-40"
                  value={form.aiMonthlyTokenBudget}
                  onChange={(e) => set('aiMonthlyTokenBudget', Number(e.target.value))}
                />
                {aiUsage.data && (
                  <span className="text-xs text-muted-foreground tnum">
                    {aiUsage.data.tokensUsed.toLocaleString()} used in {aiUsage.data.month}
                    {aiUsage.data.budget > 0 &&
                      ` (${Math.min(100, (aiUsage.data.tokensUsed / aiUsage.data.budget) * 100).toFixed(0)}%)`}
                  </span>
                )}
              </div>
            </div>
          )}

          {form.aiProvider !== 'disabled' && (
            <div className="flex items-center gap-2">
              <Checkbox
                id="weekly-batch"
                checked={form.aiWeeklyBatchEnabled}
                onCheckedChange={(c) => set('aiWeeklyBatchEnabled', c === true)}
              />
              <Label htmlFor="weekly-batch" className="font-normal">
                Weekly batch: brief the top
              </Label>
              <Input
                type="number"
                min={1}
                max={20}
                className="w-16"
                value={form.aiWeeklyBatchCount}
                onChange={(e) => set('aiWeeklyBatchCount', Number(e.target.value))}
              />
              <span className="text-sm">queue candidates automatically</span>
            </div>
          )}

          <div className="flex items-center gap-3">
            <Button variant="outline" size="sm" onClick={() => resetPrompts.mutate()}>
              Reset prompt templates to defaults
            </Button>
            <span className="text-xs text-muted-foreground">
              Section prompts are editable files under the app data folder (prompts\brief) and
              hot-reload on the next generation.
            </span>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Tax estimate inputs</CardTitle>
        </CardHeader>
        <CardContent className="grid grid-cols-[12rem_1fr] items-center gap-3">
          <Label>Filing status</Label>
          <Select
            value={form.filingStatus}
            onValueChange={(v) => set('filingStatus', v as FilingStatus)}
          >
            <SelectTrigger className="w-64">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="Single">Single</SelectItem>
              <SelectItem value="MarriedFilingJointly">Married filing jointly</SelectItem>
              <SelectItem value="HeadOfHousehold">Head of household</SelectItem>
            </SelectContent>
          </Select>
          <Label htmlFor="other-income">Other taxable income / yr</Label>
          <Tooltip>
            <TooltipTrigger asChild>
              <Input
                id="other-income"
                type="number"
                min={0}
                step={1000}
                className="w-40"
                value={form.otherIncome}
                onChange={(e) => set('otherIncome', Number(e.target.value))}
              />
            </TooltipTrigger>
            <TooltipContent className="max-w-sm">
              Wages, freelance, and other taxable income for the year — used to stack capital gains
              into brackets. The estimate ignores NIIT, AMT, credits, and the qualified-dividend
              split. Federal only.
            </TooltipContent>
          </Tooltip>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Accounts</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="rounded-md border">
            <table className="w-full text-xs">
              <thead>
                <tr className="border-b text-muted-foreground">
                  <th className="px-2 py-1.5 text-left">Name</th>
                  <th className="px-2 py-1.5 text-left">Number</th>
                  <th className="px-2 py-1.5 text-left">Treatment</th>
                  <th className="px-2 py-1.5 text-right">Positions</th>
                  <th className="px-2 py-1.5 text-right">Rows</th>
                  <th className="w-24 px-2 py-1.5"></th>
                </tr>
              </thead>
              <tbody>
                {accounts.data?.map((a) => (
                  <tr key={a.id} className="border-b border-border/50 last:border-0">
                    <td className="px-2 py-1.5">{a.displayName}</td>
                    <td className="px-2 py-1.5 tnum">{a.number}</td>
                    <td className="px-2 py-1.5">{a.treatment}</td>
                    <td className="px-2 py-1.5 text-right tnum">{a.positions}</td>
                    <td className="px-2 py-1.5 text-right tnum">{a.transactions}</td>
                    <td className="px-2 py-1.5 text-right">
                      <Button
                        variant="outline"
                        size="sm"
                        className="h-6 text-xs text-destructive"
                        onClick={() => setDeleting(a)}
                      >
                        Delete…
                      </Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </CardContent>
      </Card>

      <div className="flex justify-end">
        <Button onClick={() => save.mutate(form)} disabled={save.isPending}>
          Save settings
        </Button>
      </div>

      <ConfirmDialog
        open={deleting !== null}
        title={deleting ? `Delete ${deleting.displayName}` : ''}
        description={
          deleting
            ? deleting.positions === 0 && deleting.transactions === 0
              ? `Delete the empty account "${deleting.displayName}"?`
              : `Delete "${deleting.displayName}" AND ALL OF ITS DATA?\n\nThis removes ${deleting.positions} position(s), ${deleting.transactions} transaction(s), plus its cash flows, import batches, lots, sales, and realized history. Wash-sale adjustments for tickers held in other accounts are recomputed. Re-importing the account's CSVs brings its data back.`
            : ''
        }
        confirmLabel="Delete"
        destructive
        onConfirm={() => {
          if (deleting) deleteAccount.mutate(deleting.id)
          setDeleting(null)
        }}
        onCancel={() => setDeleting(null)}
      />
    </div>
  )
}
