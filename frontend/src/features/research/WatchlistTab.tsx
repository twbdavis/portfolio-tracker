import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { BellRing, Pencil, Plus, ShoppingCart, Trash2 } from 'lucide-react'
import { toast } from 'sonner'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'
import { money, shortDate, signedPercent } from '@/lib/format'
import type { WatchlistItem } from '@/lib/types'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { BoughtDialog, WatchlistEntryDialog } from '@/features/research/WatchlistDialogs'

function alertText(w: WatchlistItem): string {
  if (w.targetBuyPrice == null) return ''
  if (w.alertTriggeredAt) return 'TRIGGERED'
  return w.alertEnabled ? 'armed' : 'off'
}

/// The ResearchForm watchlist tab: candidates with a thesis, an optional buy
/// target, and a one-shot ≤-target alert.
export function WatchlistTab({ onOpenTicker }: { onOpenTicker: (ticker: string) => void }) {
  const queryClient = useQueryClient()
  const query = useQuery({
    queryKey: ['watchlist'],
    queryFn: () => api.get<WatchlistItem[]>('/api/watchlist'),
  })
  const [addOpen, setAddOpen] = useState(false)
  const [editing, setEditing] = useState<WatchlistItem | null>(null)
  const [buying, setBuying] = useState<WatchlistItem | null>(null)
  const [removing, setRemoving] = useState<WatchlistItem | null>(null)

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['watchlist'] })

  const rearm = useMutation({
    mutationFn: (id: number) => api.post(`/api/watchlist/${id}/rearm`),
    onSuccess: () => {
      invalidate()
      toast.success('Alert re-armed')
    },
    onError: (e) => toast.error(String(e)),
  })

  const remove = useMutation({
    mutationFn: (id: number) => api.delete(`/api/watchlist/${id}`),
    onSuccess: () => {
      invalidate()
      toast.success('Removed from watchlist')
    },
    onError: (e) => toast.error(String(e)),
  })

  if (query.isLoading)
    return (
      <div className="space-y-2 p-4">
        <Skeleton className="h-8 w-full" />
        <Skeleton className="h-40 w-full" />
      </div>
    )

  const items = query.data ?? []

  return (
    <div className="p-4">
      <div className="mb-3 flex items-center">
        <Button size="sm" variant="outline" onClick={() => setAddOpen(true)}>
          <Plus className="size-4" /> Add ticker
        </Button>
      </div>

      {items.length === 0 ? (
        <div className="flex flex-col items-center gap-2 py-24 text-center">
          <p className="text-lg font-medium">Nothing on the watchlist</p>
          <p className="max-w-md text-sm text-muted-foreground">
            Research a ticker and add it here with a thesis and a target buy price — you'll get an
            alert when it dips to your target.
          </p>
        </div>
      ) : (
        <div className="rounded-md border">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-xs text-muted-foreground">
                <th className="px-3 py-2 text-left">Ticker</th>
                <th className="px-3 py-2 text-right">Price</th>
                <th className="px-3 py-2 text-right">Target Buy</th>
                <th className="px-3 py-2 text-right">To Target %</th>
                <th className="px-3 py-2 text-left">Conviction</th>
                <th className="px-3 py-2 text-left">Added</th>
                <th className="px-3 py-2 text-left">Alert</th>
                <th className="px-3 py-2 text-left">Thesis</th>
                <th className="w-32 px-3 py-2"></th>
              </tr>
            </thead>
            <tbody>
              {items.map((w) => {
                const alert = alertText(w)
                return (
                  <tr key={w.id} className="border-b border-border/50 last:border-0">
                    <td className="px-3 py-1.5">
                      <button
                        className="font-semibold hover:underline"
                        onClick={() => onOpenTicker(w.ticker)}
                      >
                        {w.ticker}
                      </button>
                    </td>
                    <td className="px-3 py-1.5 text-right tnum">{money(w.currentPrice)}</td>
                    <td className="px-3 py-1.5 text-right tnum">{money(w.targetBuyPrice)}</td>
                    <td className="px-3 py-1.5 text-right tnum">
                      {signedPercent(w.distanceToTargetPct)}
                    </td>
                    <td className="px-3 py-1.5">{w.conviction}/5</td>
                    <td className="px-3 py-1.5 tnum">{shortDate(w.addedAt)}</td>
                    <td
                      className={cn(
                        'px-3 py-1.5 text-xs',
                        alert === 'TRIGGERED' && 'font-semibold text-warn',
                      )}
                    >
                      {alert}
                    </td>
                    <td className="max-w-64 truncate px-3 py-1.5 text-muted-foreground" title={w.thesis ?? ''}>
                      {w.thesis ?? ''}
                    </td>
                    <td className="px-3 py-1.5 text-right whitespace-nowrap">
                      <Tooltip>
                        <TooltipTrigger asChild>
                          <Button
                            variant="ghost"
                            size="icon"
                            className="size-6"
                            onClick={() => setBuying(w)}
                          >
                            <ShoppingCart className="size-3.5" />
                          </Button>
                        </TooltipTrigger>
                        <TooltipContent>I bought it</TooltipContent>
                      </Tooltip>
                      <Tooltip>
                        <TooltipTrigger asChild>
                          <Button
                            variant="ghost"
                            size="icon"
                            className="size-6"
                            onClick={() => setEditing(w)}
                          >
                            <Pencil className="size-3.5" />
                          </Button>
                        </TooltipTrigger>
                        <TooltipContent>Edit</TooltipContent>
                      </Tooltip>
                      {w.alertTriggeredAt && (
                        <Tooltip>
                          <TooltipTrigger asChild>
                            <Button
                              variant="ghost"
                              size="icon"
                              className="size-6 text-warn"
                              onClick={() => rearm.mutate(w.id)}
                            >
                              <BellRing className="size-3.5" />
                            </Button>
                          </TooltipTrigger>
                          <TooltipContent>Re-arm alert</TooltipContent>
                        </Tooltip>
                      )}
                      <Tooltip>
                        <TooltipTrigger asChild>
                          <Button
                            variant="ghost"
                            size="icon"
                            className="size-6 text-destructive"
                            onClick={() => setRemoving(w)}
                          >
                            <Trash2 className="size-3.5" />
                          </Button>
                        </TooltipTrigger>
                        <TooltipContent>Remove</TooltipContent>
                      </Tooltip>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      <WatchlistEntryDialog open={addOpen} onOpenChange={setAddOpen} existing={null} />
      <WatchlistEntryDialog
        open={editing !== null}
        onOpenChange={(o) => !o && setEditing(null)}
        existing={editing}
      />
      <BoughtDialog item={buying} onClose={() => setBuying(null)} />
      <ConfirmDialog
        open={removing !== null}
        title="Remove from watchlist"
        description={removing ? `Remove ${removing.ticker} from the watchlist?` : ''}
        confirmLabel="Remove"
        destructive
        onConfirm={() => {
          if (removing) remove.mutate(removing.id)
          setRemoving(null)
        }}
        onCancel={() => setRemoving(null)}
      />
    </div>
  )
}
