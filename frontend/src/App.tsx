import { Navigate, NavLink, Route, Routes, useLocation } from 'react-router-dom'
import { FlaskConical, Landmark, LayoutGrid, Moon, RefreshCw, Settings, Sun, Upload, Zap } from 'lucide-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useEffect, useState } from 'react'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'
import { useTheme } from '@/lib/theme'
import { useAccounts } from '@/hooks/useAccounts'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { HeightBridge } from '@/components/HeightBridge'
import { DashboardPage } from '@/pages/DashboardPage'
import { ResearchPage } from '@/pages/ResearchPage'
import { TaxesPage } from '@/pages/TaxesPage'
import { SectorsPage } from '@/pages/SectorsPage'
import { SignalsPage } from '@/pages/SignalsPage'
import { SettingsPage } from '@/pages/SettingsPage'
import { ImportWizard } from '@/features/import/ImportWizard'
import { MigrationReviewDialog } from '@/features/accounts/MigrationReviewDialog'

export default function App() {
  const { data: accounts } = useAccounts()
  const { theme, setTheme } = useTheme()
  const location = useLocation()
  const queryClient = useQueryClient()
  const [importOpen, setImportOpen] = useState(false)

  // the server owns the persisted theme; apply it once on load
  const settingsQuery = useQuery({
    queryKey: ['settings'],
    queryFn: () => api.get<{ theme: string } & Record<string, unknown>>('/api/settings'),
  })
  useEffect(() => {
    if (settingsQuery.data) setTheme(settingsQuery.data.theme === 'light' ? 'light' : 'dark')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [settingsQuery.data])

  const refreshPrices = useMutation({
    mutationFn: () => api.post<{ refreshed: boolean; error: string | null; unresolved: string[] }>('/api/quotes/refresh'),
    onSuccess: (r) => {
      queryClient.invalidateQueries({ queryKey: ['dashboard'] })
      if (r.error) toast.error(r.error)
      else if (r.unresolved.length > 0) toast.warning(`No quote for: ${r.unresolved.join(', ')}`)
    },
  })

  const onDashboard = location.pathname === '/' || location.pathname.startsWith('/account/')

  const tabClass = ({ isActive }: { isActive: boolean }) =>
    cn(
      'flex items-center gap-1.5 border-b-2 px-3 py-2 text-sm whitespace-nowrap transition-colors',
      isActive
        ? 'border-primary text-foreground'
        : 'border-transparent text-muted-foreground hover:text-foreground',
    )

  return (
    <div className="flex h-screen flex-col">
      <header className="border-b bg-card">
        <div className="flex items-center gap-2 px-4 pt-2">
          <span className="mr-2 text-sm font-semibold tracking-wide">PortfolioTracker</span>
          <nav className="flex flex-1 items-end overflow-x-auto">
            <NavLink to="/" end className={tabClass}>
              All accounts
            </NavLink>
            {accounts?.map((a) => (
              <NavLink key={a.id} to={`/account/${a.id}`} className={tabClass}>
                {a.displayName}
                {a.isRetirement && (
                  <Badge variant="secondary" className="px-1.5 py-0 text-[10px]">
                    {a.treatmentLabel}
                  </Badge>
                )}
              </NavLink>
            ))}
            <div className="mx-3 h-5 w-px self-center bg-border" />
            <NavLink to="/research" className={tabClass}>
              <FlaskConical className="size-3.5" /> Research
            </NavLink>
            <NavLink to="/sectors" className={tabClass}>
              <LayoutGrid className="size-3.5" /> Sectors
            </NavLink>
            <NavLink to="/signals" className={tabClass}>
              <Zap className="size-3.5" /> Signals
            </NavLink>
            <NavLink to="/taxes" className={tabClass}>
              <Landmark className="size-3.5" /> Taxes
            </NavLink>
          </nav>
          <div className="flex items-center gap-1 pb-1">
            <Button variant="ghost" size="sm" onClick={() => setImportOpen(true)}>
              <Upload className="size-4" /> Import
            </Button>
            <Button
              variant="ghost"
              size="sm"
              onClick={() => refreshPrices.mutate()}
              disabled={refreshPrices.isPending}
            >
              <RefreshCw className={cn('size-4', refreshPrices.isPending && 'animate-spin')} />
              Refresh
            </Button>
            <Button variant="ghost" size="icon" asChild>
              <NavLink to="/settings">
                <Settings className="size-4" />
              </NavLink>
            </Button>
            <Button
              variant="ghost"
              size="icon"
              onClick={() => {
                const next = theme === 'dark' ? 'light' : 'dark'
                setTheme(next)
                if (settingsQuery.data)
                  api
                    .put('/api/settings', { ...settingsQuery.data, theme: next })
                    .then(() => queryClient.invalidateQueries({ queryKey: ['settings'] }))
                    .catch(() => {})
              }}
            >
              {theme === 'dark' ? <Sun className="size-4" /> : <Moon className="size-4" />}
            </Button>
          </div>
        </div>
      </header>

      <main className="flex min-h-0 flex-1 flex-col overflow-y-auto">
        <Routes>
          <Route path="/" element={<DashboardPage accountId={null} />} />
          <Route path="/account/:id" element={<DashboardPage />} />
          <Route path="/research" element={<ResearchPage />} />
          <Route path="/sectors" element={<SectorsPage />} />
          <Route path="/signals" element={<SignalsPage />} />
          <Route path="/taxes" element={<TaxesPage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>

      <ImportWizard open={importOpen} onOpenChange={setImportOpen} />
      <MigrationReviewDialog />
      {onDashboard && <HeightBridge />}
    </div>
  )
}
