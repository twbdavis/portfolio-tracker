import { useMemo, useState } from 'react'
import { useParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus } from 'lucide-react'
import { api } from '@/lib/api'
import type { Dashboard } from '@/lib/types'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { Label } from '@/components/ui/label'
import { PositionsTable } from '@/features/dashboard/PositionsTable'
import { TotalsStrip } from '@/features/dashboard/TotalsStrip'
import { PositionSheet } from '@/features/dashboard/PositionSheet'
import { AddLotDialog } from '@/features/dashboard/LotDialogs'
import { timeOfDay } from '@/lib/format'

export function DashboardPage({ accountId: accountIdProp }: { accountId?: number | null }) {
  const params = useParams()
  const accountId = accountIdProp !== undefined ? accountIdProp : Number(params.id)

  const queryClient = useQueryClient()
  const [openTicker, setOpenTicker] = useState<string | null>(null)
  const [addOpen, setAddOpen] = useState(false)

  // the show-closed toggle persists in app settings, like the WinForms toolbar button
  const settingsQuery = useQuery({
    queryKey: ['settings'],
    queryFn: () => api.get<{ showClosedPositions: boolean } & Record<string, unknown>>('/api/settings'),
  })
  const [showClosedLocal, setShowClosedLocal] = useState<boolean | null>(null)
  const showClosed = showClosedLocal ?? settingsQuery.data?.showClosedPositions ?? false
  const persistShowClosed = useMutation({
    mutationFn: (v: boolean) =>
      api.put('/api/settings', { ...settingsQuery.data, showClosedPositions: v }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['settings'] }),
  })

  const query = useQuery({
    queryKey: ['dashboard', accountId ?? 'cumulative'],
    queryFn: () =>
      api.get<Dashboard>(
        accountId == null ? '/api/dashboard/cumulative' : `/api/dashboard/account/${accountId}`,
      ),
  })

  const rows = useMemo(() => {
    const all = query.data?.rows ?? []
    return showClosed ? all : all.filter((r) => r.shares !== 0)
  }, [query.data, showClosed])

  const toggleClosed = (v: boolean) => {
    setShowClosedLocal(v)
    if (settingsQuery.data) persistShowClosed.mutate(v)
  }

  if (query.isLoading)
    return (
      <div className="space-y-2 p-4">
        <Skeleton className="h-8 w-full" />
        <Skeleton className="h-64 w-full" />
      </div>
    )

  const summary = query.data?.summary
  const isEmpty = (query.data?.rows.length ?? 0) === 0

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="flex items-center gap-3 px-4 py-2">
        <Button size="sm" variant="outline" onClick={() => setAddOpen(true)}>
          <Plus className="size-4" /> Add Position
        </Button>
        <div className="ml-auto flex items-center gap-2">
          <Switch id="show-closed" checked={showClosed} onCheckedChange={toggleClosed} />
          <Label htmlFor="show-closed" className="text-xs text-muted-foreground">
            Show closed
          </Label>
          <span className="ml-4 text-xs text-muted-foreground tnum">
            {summary?.pricesAsOf
              ? `prices as of ${timeOfDay(summary.pricesAsOf)}`
              : 'no prices yet'}
            {summary && summary.unpricedCount > 0 && `  |  ${summary.unpricedCount} unpriced`}
          </span>
        </div>
      </div>

      {isEmpty ? (
        <div className="flex flex-1 flex-col items-center justify-center gap-3 py-24 text-center">
          <p className="text-lg font-medium">No positions yet</p>
          <p className="max-w-md text-sm text-muted-foreground">
            Import your Chase transactions CSV to load accounts, positions, and history — or add a
            position manually.
          </p>
        </div>
      ) : (
        <div data-grow-region className="min-h-0 flex-1 overflow-auto">
          <PositionsTable rows={rows} onOpen={(t) => setOpenTicker(t)} accountId={accountId} />
        </div>
      )}

      {summary && <TotalsStrip summary={summary} accountId={accountId} />}

      <PositionSheet
        ticker={openTicker}
        accountId={accountId}
        onClose={() => setOpenTicker(null)}
      />
      <AddLotDialog open={addOpen} onOpenChange={setAddOpen} defaultAccountId={accountId} />
    </div>
  )
}
