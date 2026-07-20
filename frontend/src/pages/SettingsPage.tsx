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

type FilingStatus = 'Single' | 'MarriedFilingJointly' | 'HeadOfHousehold'

interface Settings {
  finnhubApiKey: string | null
  refreshIntervalSeconds: number
  showClosedPositions: boolean
  theme: string
  filingStatus: FilingStatus
  otherIncome: number
  startWithWindows: boolean
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
