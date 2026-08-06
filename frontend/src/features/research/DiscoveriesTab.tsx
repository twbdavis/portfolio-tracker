import { useMemo } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Check, ChevronDown, Compass, X } from 'lucide-react'
import { toast } from 'sonner'
import { api, ApiError } from '@/lib/api'
import { shortDate } from '@/lib/format'
import type { Discovery, UniverseInfo } from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'

/// The peer-discovery inbox: tickers surfaced by one-hop peer expansion of
/// held/watchlist/signal tickers, grouped by which ticker suggested them.
/// Accepting enqueues a full crawl; ignoring is permanent (never resurfaces).
export function DiscoveriesTab({ onOpenTicker }: { onOpenTicker: (ticker: string) => void }) {
  const queryClient = useQueryClient()

  const discoveries = useQuery({
    queryKey: ['discoveries'],
    queryFn: () => api.get<Discovery[]>('/api/discoveries'),
  })
  const universes = useQuery({
    queryKey: ['universes'],
    queryFn: () => api.get<UniverseInfo[]>('/api/universes'),
  })

  const accept = useMutation({
    mutationFn: ({ ticker, universeId }: { ticker: string; universeId: number | null }) =>
      api.post(`/api/discoveries/${ticker}/accept`, { universeId }),
    onSuccess: (_, { ticker }) => {
      toast.success(`${ticker} accepted — full crawl queued`)
      queryClient.invalidateQueries({ queryKey: ['discoveries'] })
      queryClient.invalidateQueries({ queryKey: ['universes'] })
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : String(e)),
  })

  const ignore = useMutation({
    mutationFn: (ticker: string) => api.post(`/api/discoveries/${ticker}/ignore`),
    onSuccess: (_, ticker) => {
      toast.success(`${ticker} ignored — it won't resurface`)
      queryClient.invalidateQueries({ queryKey: ['discoveries'] })
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : String(e)),
  })

  const bySource = useMemo(() => {
    const groups = new Map<string, Discovery[]>()
    for (const d of discoveries.data ?? []) {
      const list = groups.get(d.sourceTicker) ?? []
      list.push(d)
      groups.set(d.sourceTicker, list)
    }
    return Array.from(groups.entries()).sort(([a], [b]) => a.localeCompare(b))
  }, [discoveries.data])

  if (discoveries.isLoading)
    return <div className="p-8 text-center text-sm text-muted-foreground">Loading…</div>

  if (bySource.length === 0)
    return (
      <div className="mx-auto max-w-lg p-8 text-center">
        <Compass className="mx-auto mb-3 size-8 text-muted-foreground" />
        <p className="text-sm text-muted-foreground">
          No new discoveries. The nightly crawl expands peers of your held, watchlisted, and
          signal-flagged tickers (one hop) and anything not already tracked lands here for
          review.
        </p>
      </div>
    )

  return (
    <div className="flex flex-col gap-4 p-4">
      <p className="text-xs text-muted-foreground">
        Peers of your held, watchlisted, and signal-flagged tickers that aren&apos;t in any
        universe yet. Accept to warehouse and rank them; ignore to never see them again.
      </p>
      {bySource.map(([source, items]) => (
        <div key={source}>
          <h3 className="mb-2 text-sm font-semibold">
            via {source}{' '}
            <span className="font-normal text-muted-foreground">({items.length})</span>
          </h3>
          <div className="flex flex-col gap-1.5">
            {items.map((d) => (
              <div
                key={d.ticker}
                className="flex items-center justify-between gap-2 rounded-md border bg-card px-3 py-2"
              >
                <div className="flex items-center gap-2">
                  <button
                    className="font-semibold hover:underline"
                    onClick={() => onOpenTicker(d.ticker)}
                  >
                    {d.ticker}
                  </button>
                  <Badge variant="secondary" className="px-1.5 py-0 text-[10px]">
                    discovered {shortDate(d.discoveredAt)}
                  </Badge>
                </div>
                <div className="flex items-center gap-1">
                  <div className="flex">
                    <Button
                      variant="outline"
                      size="sm"
                      className="h-7 rounded-r-none px-2 text-xs"
                      onClick={() => accept.mutate({ ticker: d.ticker, universeId: null })}
                      disabled={accept.isPending}
                    >
                      <Check className="size-3.5" /> Accept
                    </Button>
                    <DropdownMenu>
                      <DropdownMenuTrigger asChild>
                        <Button
                          variant="outline"
                          size="sm"
                          className="h-7 rounded-l-none border-l-0 px-1"
                        >
                          <ChevronDown className="size-3.5" />
                        </Button>
                      </DropdownMenuTrigger>
                      <DropdownMenuContent align="end">
                        <DropdownMenuItem
                          onClick={() => accept.mutate({ ticker: d.ticker, universeId: null })}
                        >
                          Into &quot;Discovered&quot; universe (default)
                        </DropdownMenuItem>
                        {universes.data?.map((u) => (
                          <DropdownMenuItem
                            key={u.id}
                            onClick={() => accept.mutate({ ticker: d.ticker, universeId: u.id })}
                          >
                            Into {u.name}
                          </DropdownMenuItem>
                        ))}
                      </DropdownMenuContent>
                    </DropdownMenu>
                  </div>
                  <Button
                    variant="ghost"
                    size="sm"
                    className="h-7 px-2 text-xs text-muted-foreground"
                    onClick={() => ignore.mutate(d.ticker)}
                    disabled={ignore.isPending}
                  >
                    <X className="size-3.5" /> Ignore
                  </Button>
                </div>
              </div>
            ))}
          </div>
        </div>
      ))}
    </div>
  )
}
