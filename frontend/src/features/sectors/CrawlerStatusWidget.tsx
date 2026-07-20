import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Loader2, RefreshCw } from 'lucide-react'
import { toast } from 'sonner'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'
import { timeOfDay, shortDate } from '@/lib/format'
import type { CrawlerStatus } from '@/lib/types'
import { Button } from '@/components/ui/button'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'

/// Compact warehouse-freshness readout: tickers fresh vs stale, last crawl,
/// API budget in use, plus the on-demand "Refresh data now" trigger.
export function CrawlerStatusWidget() {
  const queryClient = useQueryClient()
  const status = useQuery<CrawlerStatus>({
    queryKey: ['crawler-status'],
    queryFn: () => api.get<CrawlerStatus>('/api/crawler/status'),
    refetchInterval: (query) => (query.state.data?.running ? 2000 : 30000),
  })

  const run = useMutation({
    mutationFn: () => api.post<{ started: boolean }>('/api/crawler/run'),
    onSuccess: () => {
      toast.success('Crawl started')
      queryClient.invalidateQueries({ queryKey: ['crawler-status'] })
    },
    onError: (e) => toast.error(String(e)),
  })

  const s = status.data
  if (!s) return null

  const lastCrawl = s.lastCompletedAt
    ? `${shortDate(s.lastCompletedAt)} ${timeOfDay(s.lastCompletedAt)}`
    : 'never'

  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1 rounded-md border bg-card px-3 py-1.5 text-xs">
      {s.running ? (
        <span className="flex items-center gap-1.5 font-medium">
          <Loader2 className="size-3.5 animate-spin" />
          Crawling {s.processed}/{s.total}
          {s.currentTicker && <span className="text-muted-foreground">({s.currentTicker})</span>}
        </span>
      ) : (
        <Tooltip>
          <TooltipTrigger asChild>
            <span className={cn('font-medium', s.staleTickers > 0 && 'text-warn')}>
              {s.freshTickers}/{s.totalTickers} tickers fresh
            </span>
          </TooltipTrigger>
          <TooltipContent>
            {s.pendingItems} data items pending; crawling runs automatically after the 4:30 PM ET
            close on weekdays, or on demand
          </TooltipContent>
        </Tooltip>
      )}
      <span className="text-muted-foreground">last crawl {lastCrawl}</span>
      <span className="text-muted-foreground">
        budget {s.callsLastMinute}/{s.budgetPerMinute} calls/min
      </span>
      {s.lastError && <span className="text-loss">{s.lastError}</span>}
      <Button
        variant="ghost"
        size="sm"
        className="h-6 px-2 text-xs"
        disabled={s.running || run.isPending}
        onClick={() => run.mutate()}
      >
        <RefreshCw className="size-3" /> Refresh data now
      </Button>
    </div>
  )
}
