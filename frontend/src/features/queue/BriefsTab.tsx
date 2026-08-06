import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AlertTriangle, Bot, ChevronDown, ChevronRight } from 'lucide-react'
import { api } from '@/lib/api'
import { shortDate, timeOfDay } from '@/lib/format'
import type { BriefSummary, BriefTraceEntry, ResearchBrief } from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { MarkdownLite, TracePanel } from '@/features/queue/BriefDialog'

/// Every AI brief ever generated, newest first. This is a history viewer:
/// each row opens the exact stored brief (older briefs for the same ticker
/// included) — generation itself stays on the queue rows.
export function BriefsTab() {
  const [openId, setOpenId] = useState<number | null>(null)

  const briefs = useQuery({
    queryKey: ['briefs-all'],
    queryFn: () => api.get<BriefSummary[]>('/api/briefs'),
  })

  return (
    <div className="flex flex-col gap-4 p-4">
      {briefs.isLoading && (
        <div className="rounded-lg border bg-card p-8 text-center text-sm text-muted-foreground">
          Loading…
        </div>
      )}

      {briefs.data && briefs.data.length === 0 && (
        <div className="flex flex-col items-center gap-2 rounded-lg border bg-card p-8">
          <Bot className="size-5 text-muted-foreground" />
          <p className="text-sm text-muted-foreground">
            No briefs yet — generate one from any Queue row&apos;s &quot;AI brief&quot; button and
            it will be archived here.
          </p>
        </div>
      )}

      {briefs.data && briefs.data.length > 0 && (
        <div className="rounded-md border">
          <table className="w-full text-xs">
            <thead>
              <tr className="border-b text-muted-foreground">
                <th className="px-3 py-1.5 text-left font-medium">Ticker</th>
                <th className="px-3 py-1.5 text-left font-medium">Generated</th>
                <th className="px-3 py-1.5 text-left font-medium">Model</th>
                <th className="px-3 py-1.5 text-right font-medium">Tokens</th>
                <th className="px-3 py-1.5 text-left font-medium"></th>
              </tr>
            </thead>
            <tbody>
              {briefs.data.map((b) => (
                <tr
                  key={b.id}
                  className="cursor-pointer border-b border-border/50 last:border-0 hover:bg-muted/40"
                  onClick={() => setOpenId(b.id)}
                >
                  <td className="px-3 py-1.5 font-semibold">{b.ticker}</td>
                  <td className="px-3 py-1.5 whitespace-nowrap tnum">
                    {shortDate(b.createdAt)} {timeOfDay(b.createdAt)}
                  </td>
                  <td className="px-3 py-1.5 text-muted-foreground">{b.model}</td>
                  <td className="px-3 py-1.5 text-right tnum">
                    {b.tokensUsed > 0 ? b.tokensUsed.toLocaleString() : '—'}
                  </td>
                  <td className="px-3 py-1.5">
                    {b.needsReview && (
                      <Badge variant="outline" className="gap-1 px-1.5 py-0 text-[10px] text-warn">
                        <AlertTriangle className="size-3" /> needs review
                      </Badge>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <BriefViewerDialog briefId={openId} onClose={() => setOpenId(null)} />
    </div>
  )
}

/// Read-only viewer for one stored brief — unlike the queue's BriefDialog it
/// shows exactly the requested brief, not the ticker's latest.
function BriefViewerDialog({ briefId, onClose }: { briefId: number | null; onClose: () => void }) {
  const [traceOpen, setTraceOpen] = useState(false)

  const brief = useQuery({
    queryKey: ['brief', briefId],
    queryFn: () => api.get<ResearchBrief>(`/api/briefs/${briefId}`),
    enabled: briefId !== null,
  })
  const trace = useQuery({
    queryKey: ['brief-trace', briefId],
    queryFn: () => api.get<BriefTraceEntry[]>(`/api/briefs/${briefId}/trace`),
    enabled: traceOpen && briefId !== null,
  })
  const data = brief.data

  return (
    <Dialog open={briefId !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="flex max-h-[85vh] flex-col overflow-hidden sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Bot className="size-4" /> AI research brief — {data?.ticker ?? '…'}
          </DialogTitle>
          <DialogDescription>
            AI-generated from your local data as of the generation date — not advice.
          </DialogDescription>
        </DialogHeader>

        {data ? (
          <>
            <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
              <Badge variant="secondary" className="text-[10px]">
                AI-generated · not advice
              </Badge>
              {data.needsReview && (
                <Badge variant="outline" className="gap-1 text-[10px] text-warn">
                  <AlertTriangle className="size-3" /> needs review
                </Badge>
              )}
              <span>
                {shortDate(data.createdAt)} {timeOfDay(data.createdAt)} · {data.model}
                {data.tokensUsed > 0 && <> · {data.tokensUsed.toLocaleString()} tokens</>}
              </span>
            </div>
            <div className="min-h-0 flex-1 overflow-y-auto rounded-md border p-4">
              <MarkdownLite text={data.briefMarkdown} />
              <div className="mt-4 space-y-2 border-t pt-2">
                <button
                  className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
                  onClick={() => setTraceOpen(!traceOpen)}
                >
                  {traceOpen ? (
                    <ChevronDown className="size-3" />
                  ) : (
                    <ChevronRight className="size-3" />
                  )}
                  View generation trace ({data.model})
                </button>
                {traceOpen && <TracePanel entries={trace.data} />}
              </div>
            </div>
          </>
        ) : (
          <div className="py-8 text-center text-sm text-muted-foreground">Loading…</div>
        )}
      </DialogContent>
    </Dialog>
  )
}
