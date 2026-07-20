import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus, Trash2, X } from 'lucide-react'
import { toast } from 'sonner'
import { api } from '@/lib/api'
import type { UniverseInfo } from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { ScrollArea } from '@/components/ui/scroll-area'
import { Textarea } from '@/components/ui/textarea'
import { ConfirmDialog } from '@/components/ConfirmDialog'

/// Create/edit/delete universes and their memberships. Seeded universes are a
/// starting point — they rename and delete like anything else.
export function UniverseManagerDialog({
  open,
  onOpenChange,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
}) {
  const queryClient = useQueryClient()
  const [selectedId, setSelectedId] = useState<number | null>(null)
  const [newName, setNewName] = useState('')
  const [addText, setAddText] = useState('')
  const [deleting, setDeleting] = useState<UniverseInfo | null>(null)

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ['universes'] })
    queryClient.invalidateQueries({ queryKey: ['universe-members'] })
    queryClient.invalidateQueries({ queryKey: ['crawler-status'] })
    queryClient.invalidateQueries({ queryKey: ['sectors'] })
  }

  const universes = useQuery<UniverseInfo[]>({
    queryKey: ['universes'],
    queryFn: () => api.get<UniverseInfo[]>('/api/universes'),
    enabled: open,
  })
  const selected = universes.data?.find((u) => u.id === selectedId) ?? universes.data?.[0] ?? null

  const members = useQuery<string[]>({
    queryKey: ['universe-members', selected?.id],
    queryFn: () => api.get<string[]>(`/api/universes/${selected!.id}/members`),
    enabled: open && selected != null,
  })

  const create = useMutation({
    mutationFn: (name: string) => api.post<UniverseInfo>('/api/universes', { name }),
    onSuccess: (u) => {
      setNewName('')
      setSelectedId(u.id)
      invalidate()
    },
    onError: (e) => toast.error(String(e)),
  })

  const remove = useMutation({
    mutationFn: (id: number) => api.delete(`/api/universes/${id}`),
    onSuccess: () => {
      setSelectedId(null)
      invalidate()
    },
    onError: (e) => toast.error(String(e)),
  })

  const addTickers = useMutation({
    mutationFn: (tickers: string[]) =>
      api.post(`/api/universes/${selected!.id}/members`, { tickers }),
    onSuccess: () => {
      setAddText('')
      invalidate()
    },
    onError: (e) => toast.error(String(e)),
  })

  const addFrom = useMutation({
    mutationFn: (source: 'from-holdings' | 'from-watchlist') =>
      api.post(`/api/universes/${selected!.id}/members/${source}`),
    onSuccess: invalidate,
    onError: (e) => toast.error(String(e)),
  })

  const removeTicker = useMutation({
    mutationFn: (ticker: string) =>
      api.delete(`/api/universes/${selected!.id}/members/${encodeURIComponent(ticker)}`),
    onSuccess: invalidate,
    onError: (e) => toast.error(String(e)),
  })

  const crawlUniverse = useMutation({
    mutationFn: () => api.post(`/api/crawler/run?universeId=${selected!.id}`),
    onSuccess: () => {
      toast.success(`Crawl started for ${selected!.name}`)
      queryClient.invalidateQueries({ queryKey: ['crawler-status'] })
    },
    onError: (e) => toast.error(String(e)),
  })

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      {/* the base component pins sm:max-w-lg, so the override needs the sm:
          variant too — a bare max-w-2xl loses and the two-column grid gets
          crushed into 512px */}
      <DialogContent className="max-h-[85vh] overflow-y-auto sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Universes</DialogTitle>
          <DialogDescription>
            Bounded ticker lists the background crawler keeps warehoused for sector analysis.
          </DialogDescription>
        </DialogHeader>

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-[220px_minmax(0,1fr)]">
          <div className="flex flex-col gap-2">
            <ScrollArea className="h-64 rounded-md border">
              <div className="p-1">
                {universes.data?.map((u) => (
                  <button
                    key={u.id}
                    onClick={() => setSelectedId(u.id)}
                    className={`flex w-full items-center justify-between rounded px-2 py-1.5 text-left text-sm ${
                      selected?.id === u.id ? 'bg-accent' : 'hover:bg-accent/50'
                    }`}
                  >
                    <span className="truncate">{u.name}</span>
                    <span className="ml-2 flex shrink-0 items-center gap-1">
                      {u.isSeeded && (
                        <Badge variant="secondary" className="px-1 py-0 text-[10px]">
                          seed
                        </Badge>
                      )}
                      <span className="text-xs text-muted-foreground">{u.memberCount}</span>
                    </span>
                  </button>
                ))}
              </div>
            </ScrollArea>
            <div className="flex gap-1">
              <Input
                placeholder="New universe…"
                value={newName}
                onChange={(e) => setNewName(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && newName.trim()) create.mutate(newName.trim())
                }}
              />
              <Button
                variant="outline"
                size="icon"
                disabled={!newName.trim() || create.isPending}
                onClick={() => create.mutate(newName.trim())}
              >
                <Plus className="size-4" />
              </Button>
            </div>
          </div>

          {selected ? (
            <div className="flex min-w-0 flex-col gap-2">
              <div className="flex flex-wrap items-center justify-between gap-1">
                <span className="min-w-0 truncate font-medium">{selected.name}</span>
                <div className="flex shrink-0 gap-1">
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={() => crawlUniverse.mutate()}
                    disabled={crawlUniverse.isPending}
                  >
                    Refresh now
                  </Button>
                  <Button variant="ghost" size="icon" onClick={() => setDeleting(selected)}>
                    <Trash2 className="size-4 text-destructive" />
                  </Button>
                </div>
              </div>
              <ScrollArea className="h-48 rounded-md border p-2">
                <div className="flex flex-wrap gap-1">
                  {members.data?.map((t) => (
                    <Badge key={t} variant="secondary" className="gap-1 pr-1">
                      {t}
                      <button onClick={() => removeTicker.mutate(t)} aria-label={`Remove ${t}`}>
                        <X className="size-3 opacity-60 hover:opacity-100" />
                      </button>
                    </Badge>
                  ))}
                  {members.data?.length === 0 && (
                    <span className="text-sm text-muted-foreground">No tickers yet.</span>
                  )}
                </div>
              </ScrollArea>
              <Textarea
                placeholder="Add tickers (comma, space, or newline separated)…"
                value={addText}
                onChange={(e) => setAddText(e.target.value)}
                rows={2}
              />
              <div className="flex flex-wrap gap-1">
                <Button
                  variant="outline"
                  size="sm"
                  disabled={!addText.trim() || addTickers.isPending}
                  onClick={() => addTickers.mutate(addText.split(/[\s,;]+/).filter(Boolean))}
                >
                  Add tickers
                </Button>
                <Button variant="outline" size="sm" onClick={() => addFrom.mutate('from-holdings')}>
                  Add my holdings
                </Button>
                <Button variant="outline" size="sm" onClick={() => addFrom.mutate('from-watchlist')}>
                  Add watchlist
                </Button>
              </div>
            </div>
          ) : (
            <div className="flex items-center justify-center text-sm text-muted-foreground">
              Select or create a universe.
            </div>
          )}
        </div>

        <ConfirmDialog
          open={deleting !== null}
          title={deleting ? `Delete ${deleting.name}` : ''}
          description="Removes the universe and its member list. Warehoused metrics for these tickers are kept (other universes may share them)."
          confirmLabel="Delete"
          destructive
          onConfirm={() => {
            if (deleting) remove.mutate(deleting.id)
            setDeleting(null)
          }}
          onCancel={() => setDeleting(null)}
        />
      </DialogContent>
    </Dialog>
  )
}
