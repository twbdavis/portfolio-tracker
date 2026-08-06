import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, Bot, ChevronDown, ChevronRight, Sparkles } from 'lucide-react'
import { toast } from 'sonner'
import { api, ApiError } from '@/lib/api'
import { cn } from '@/lib/utils'
import { shortDate, timeOfDay } from '@/lib/format'
import type { AiUsage, BriefRunStatus, BriefTraceEntry, ResearchBrief } from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'

/// Constrained markdown for briefs (##, bullets, **bold**) rendered without
/// HTML injection — the model output never touches innerHTML.
export function MarkdownLite({ text }: { text: string }) {
  const lines = text.split(/\r?\n/)
  const blocks: React.ReactNode[] = []
  let bullets: string[] = []
  const flushBullets = (key: string) => {
    if (bullets.length === 0) return
    blocks.push(
      <ul key={key} className="my-1.5 ml-4 list-disc space-y-1">
        {bullets.map((b, i) => (
          <li key={i}>
            <Inline text={b} />
          </li>
        ))}
      </ul>,
    )
    bullets = []
  }
  lines.forEach((raw, i) => {
    const line = raw.trimEnd()
    const bullet = /^\s*[-*]\s+(.*)$/.exec(line)
    if (bullet) {
      bullets.push(bullet[1])
      return
    }
    flushBullets(`ul-${i}`)
    if (line.startsWith('## ')) {
      blocks.push(
        <h3 key={i} className="mt-4 mb-1 text-sm font-semibold first:mt-0">
          {line.slice(3)}
        </h3>,
      )
    } else if (line.startsWith('# ')) {
      blocks.push(
        <h3 key={i} className="mt-4 mb-1 text-sm font-semibold first:mt-0">
          {line.slice(2)}
        </h3>,
      )
    } else if (line.startsWith('> ')) {
      blocks.push(
        <p key={i} className="my-1.5 border-l-2 border-warn/60 pl-2 text-warn">
          <Inline text={line.slice(2)} />
        </p>,
      )
    } else if (line.trim().length > 0) {
      blocks.push(
        <p key={i} className="my-1.5">
          <Inline text={line} />
        </p>,
      )
    }
  })
  flushBullets('ul-end')
  return <div className="text-sm leading-relaxed">{blocks}</div>
}

function Inline({ text }: { text: string }) {
  const parts = text.split(/(\*\*[^*]+\*\*)/g)
  return (
    <>
      {parts.map((p, i) =>
        p.startsWith('**') && p.endsWith('**') ? <strong key={i}>{p.slice(2, -2)}</strong> : p,
      )}
    </>
  )
}

/// Pulls the "Why it surfaced" section out of a brief for the watchlist
/// thesis prefill. Briefs generated after 2026-08-05 no longer carry the
/// section (it was cut from the pipeline); callers fall back to the brief's
/// opening text.
export function whyItSurfaced(markdown: string): string | null {
  const match = /##\s*Why it surfaced\s*\n([\s\S]*?)(?=\n##\s|$)/i.exec(markdown)
  const body = match?.[1]?.trim()
  return body && body.length > 0 ? body : null
}

export function BriefDialog({
  ticker,
  open,
  onOpenChange,
}: {
  ticker: string | null
  open: boolean
  onOpenChange: (open: boolean) => void
}) {
  const queryClient = useQueryClient()
  const [snapshotOpen, setSnapshotOpen] = useState(false)
  const [traceOpen, setTraceOpen] = useState(false)
  const [generating, setGenerating] = useState(false)

  const briefs = useQuery({
    queryKey: ['briefs', ticker],
    queryFn: () => api.get<ResearchBrief[]>(`/api/research/${ticker}/briefs`),
    enabled: open && !!ticker,
  })
  const usage = useQuery({
    queryKey: ['ai-usage'],
    queryFn: () => api.get<AiUsage>('/api/ai/usage'),
    enabled: open,
  })
  const latest = briefs.data?.[0] ?? null

  // per-stage pipeline progress: generation runs server-side, so the server's
  // status is the source of truth, checked whenever the dialog is open — a
  // run started earlier still shows its progress after navigating away and
  // back, even though this component's state was lost in between
  const status = useQuery({
    queryKey: ['brief-status'],
    queryFn: () => api.get<BriefRunStatus>('/api/briefs/status'),
    enabled: open,
    refetchInterval: (query) => (query.state.data?.active || generating ? 1200 : false),
  })
  const runningHere = !!status.data?.active && status.data.ticker === ticker
  const runningElsewhere = !!status.data?.active && status.data.ticker !== ticker

  // `generating` only bridges the gap between kicking off a run and the first
  // status poll confirming it; `wasRunning` remembers a confirmed run so its
  // completion (success or error) is announced exactly once. `kickedOffAt`
  // guards the bridge: the status cache still holds the pre-click idle
  // snapshot when `generating` flips on, and treating that stale idle as
  // "finished" announced success the moment Generate was pressed.
  const wasRunning = useRef(false)
  const kickedOffAt = useRef<number | null>(null)
  useEffect(() => {
    if (runningHere) {
      wasRunning.current = true
      setGenerating(false)
      return
    }
    if (!status.data || status.data.active) return
    if (!wasRunning.current) {
      // never saw the run active: only an idle status fetched AFTER the
      // kick-off proves it started and finished between polls
      if (!generating) return
      if (kickedOffAt.current === null || status.dataUpdatedAt <= kickedOffAt.current) return
    }
    wasRunning.current = false
    kickedOffAt.current = null
    setGenerating(false)
    if (status.data.lastError) toast.error(status.data.lastError)
    else toast.success(`Brief generated for ${ticker}`)
    queryClient.invalidateQueries({ queryKey: ['briefs', ticker] })
    queryClient.invalidateQueries({ queryKey: ['briefs-all'] })
    queryClient.invalidateQueries({ queryKey: ['ai-usage'] })
    queryClient.invalidateQueries({ queryKey: ['queue'] })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status.data, status.dataUpdatedAt, generating, runningHere])

  const snapshot = useQuery({
    queryKey: ['brief-snapshot', latest?.id],
    queryFn: () => api.get<ResearchBrief>(`/api/briefs/${latest!.id}`),
    enabled: snapshotOpen && !!latest,
  })
  const trace = useQuery({
    queryKey: ['brief-trace', latest?.id],
    queryFn: () => api.get<BriefTraceEntry[]>(`/api/briefs/${latest!.id}/trace`),
    enabled: traceOpen && !!latest,
  })

  const generate = useMutation({
    mutationFn: (force: boolean) =>
      api.post<ResearchBrief | { started: boolean }>(
        `/api/research/${ticker}/brief${force ? '?force=true' : ''}`,
      ),
    onSuccess: (resp) => {
      if (resp && 'id' in resp) {
        // idempotency hit: the data hasn't changed since this brief
        toast.info('Brief is already up to date for the current data — use Regenerate to force.')
        queryClient.invalidateQueries({ queryKey: ['briefs', ticker] })
      } else {
        kickedOffAt.current = Date.now()
        setGenerating(true)
        // poll immediately so the progress panel appears without waiting a tick
        queryClient.invalidateQueries({ queryKey: ['brief-status'] })
      }
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : String(e)),
  })

  const addToWatchlist = useMutation({
    mutationFn: (brief: ResearchBrief) => {
      const why = whyItSurfaced(brief.briefMarkdown)
      return api.post('/api/watchlist', {
        ticker: brief.ticker,
        name: null,
        thesis: `[AI-drafted from the ${shortDate(brief.createdAt)} research brief — edit before relying on it]\n${why ?? brief.briefMarkdown.slice(0, 500)}`,
        conviction: 3,
        targetBuyPrice: null,
        alertEnabled: false,
      })
    },
    onSuccess: (_, brief) =>
      toast.success(`${brief.ticker} added to watchlist (AI-drafted thesis)`),
    onError: (e) => toast.error(e instanceof ApiError ? e.message : String(e)),
  })

  const busy = generating || generate.isPending || runningHere
  const canGenerate = !!usage.data?.enabled && !busy && !runningElsewhere

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="flex max-h-[85vh] flex-col overflow-hidden sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Bot className="size-4" /> AI research brief — {ticker}
          </DialogTitle>
          <DialogDescription>
            AI-generated from your local data — not advice. Each section is generated separately,
            grounding-checked against the exact data it saw, and repaired or withheld on failure.
          </DialogDescription>
        </DialogHeader>

        {usage.data && (
          <div className="flex flex-wrap items-center gap-3 text-xs text-muted-foreground">
            {usage.data.provider === 'openwebui' && (
              <Badge variant="outline" className="text-[10px]">
                Local AI server
              </Badge>
            )}
            {usage.data.budgetEnforced && usage.data.budget > 0 && (
              <>
                <div className="h-1.5 w-40 overflow-hidden rounded-full bg-muted">
                  <div
                    className="h-full rounded-full bg-primary"
                    style={{
                      width: `${Math.min(100, (usage.data.tokensUsed / usage.data.budget) * 100)}%`,
                    }}
                  />
                </div>
                <span className="tnum">
                  {usage.data.tokensUsed.toLocaleString()} / {usage.data.budget.toLocaleString()}{' '}
                  tokens this month
                </span>
              </>
            )}
            {!usage.data.enabled && (
              <Badge variant="outline" className="text-warn">
                {usage.data.provider === 'disabled'
                  ? 'Briefs disabled — choose a provider in Settings'
                  : 'Provider not fully configured — check Settings'}
              </Badge>
            )}
          </div>
        )}

        {runningElsewhere && (
          <p className="rounded-md border bg-card px-3 py-2 text-xs text-muted-foreground">
            A brief for {status.data?.ticker} is generating — briefs run one at a time, so Generate
            unlocks here when it finishes.
          </p>
        )}

        {busy && (
          <div className="flex items-center gap-3 rounded-md border bg-card px-3 py-2 text-xs">
            <Sparkles className="size-3.5 animate-pulse" />
            <div className="flex-1">
              <div className="font-medium">
                {status.data?.stage ? `Generating: ${status.data.stage}` : 'Starting pipeline…'}
              </div>
              <div className="text-muted-foreground">
                Section {status.data?.sectionIndex ?? 0} of {status.data?.totalSections ?? 4} —
                sequential calls to the model, this can take a few minutes.
              </div>
            </div>
            <span className="tnum text-muted-foreground">
              {status.data?.sectionIndex ?? 0}/{status.data?.totalSections ?? 4}
            </span>
          </div>
        )}

        {briefs.isLoading ? (
          <div className="py-8 text-center text-sm text-muted-foreground">Loading…</div>
        ) : latest ? (
          <>
            <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
              <Badge variant="secondary" className="text-[10px]">
                AI-generated · not advice
              </Badge>
              {latest.needsReview && (
                <Badge variant="outline" className="gap-1 text-[10px] text-warn">
                  <AlertTriangle className="size-3" /> needs review
                </Badge>
              )}
              <span>
                {shortDate(latest.createdAt)} {timeOfDay(latest.createdAt)} · {latest.model}
                {latest.tokensUsed > 0 && <> · {latest.tokensUsed.toLocaleString()} tokens</>}
              </span>
            </div>
            {latest.needsReview && (
              <p className="rounded-md border border-warn/40 bg-warn/5 px-3 py-1.5 text-xs text-muted-foreground">
                One or more sections contain numbers the grounding check could not verify against
                the input data — they are kept, with the specific figures listed in a footnote.
                The generation trace below shows the attempts and exact violations.
              </p>
            )}
            <div className="min-h-0 flex-1 overflow-y-auto rounded-md border p-4">
              <MarkdownLite text={latest.briefMarkdown} />
              <div className="mt-4 space-y-2 border-t pt-2">
                <button
                  className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
                  onClick={() => setSnapshotOpen(!snapshotOpen)}
                >
                  {snapshotOpen ? (
                    <ChevronDown className="size-3" />
                  ) : (
                    <ChevronRight className="size-3" />
                  )}
                  Exact input snapshot the pipeline sliced per section
                </button>
                {snapshotOpen && (
                  <pre className="max-h-64 overflow-auto rounded bg-muted/50 p-2 text-[10px] leading-snug">
                    {snapshot.data?.inputSnapshotJson ?? 'Loading…'}
                  </pre>
                )}
                <button
                  className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
                  onClick={() => setTraceOpen(!traceOpen)}
                >
                  {traceOpen ? (
                    <ChevronDown className="size-3" />
                  ) : (
                    <ChevronRight className="size-3" />
                  )}
                  View generation trace ({latest.model})
                </button>
                {traceOpen && <TracePanel entries={trace.data} />}
              </div>
            </div>
            <div className="flex flex-wrap items-center justify-between gap-2">
              <Button
                variant="outline"
                size="sm"
                onClick={() => addToWatchlist.mutate(latest)}
                disabled={addToWatchlist.isPending}
              >
                Add to watchlist with AI-drafted thesis
              </Button>
              <Button
                variant="outline"
                size="sm"
                onClick={() => generate.mutate(true)}
                disabled={!canGenerate}
              >
                <Sparkles className="size-3.5" />
                {busy ? 'Generating…' : 'Regenerate'}
              </Button>
            </div>
          </>
        ) : (
          <div className="flex flex-col items-center gap-3 py-8">
            <p className="text-sm text-muted-foreground">No brief for {ticker} yet.</p>
            <Button onClick={() => generate.mutate(false)} disabled={!canGenerate}>
              <Sparkles className="size-4" />
              {busy ? 'Generating…' : 'Generate brief'}
            </Button>
            <p className="max-w-sm text-center text-xs text-muted-foreground">
              The brief leads with a 1-100 Buy score anchored to the stock&apos;s two-month return
              history, then key numbers, bear case, and research questions — each generated from
              only its slice of your local data. Every number is verified against that slice;
              failing sections get one repair, then are withheld.
            </p>
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}

export function TracePanel({ entries }: { entries: BriefTraceEntry[] | undefined }) {
  if (!entries) return <div className="text-xs text-muted-foreground">Loading…</div>
  if (entries.length === 0)
    return (
      <div className="text-xs text-muted-foreground">
        No trace stored (sections may have been resumed from an earlier run).
      </div>
    )
  return (
    <div className="space-y-2">
      {entries.map((e) => (
        <TraceEntry key={e.id} entry={e} />
      ))}
    </div>
  )
}

function TraceEntry({ entry }: { entry: BriefTraceEntry }) {
  const [open, setOpen] = useState(false)
  let violations: { kind: string; detail: string }[] = []
  try {
    violations = JSON.parse(entry.violationsJson)
  } catch {
    /* legacy/empty */
  }
  return (
    <div className="rounded border bg-muted/30 p-2 text-[11px]">
      <button className="flex w-full items-center gap-2 text-left" onClick={() => setOpen(!open)}>
        {open ? <ChevronDown className="size-3" /> : <ChevronRight className="size-3" />}
        <span className="font-medium">{entry.section}</span>
        <Badge variant="outline" className="px-1 py-0 text-[9px]">
          {entry.stage}
        </Badge>
        <span className={cn('ml-auto font-medium', entry.passed ? 'text-gain' : 'text-loss')}>
          {entry.passed ? 'passed' : 'failed'}
        </span>
      </button>
      {!entry.passed && violations.length > 0 && (
        <ul className="mt-1 ml-5 list-disc text-warn">
          {violations.map((v, i) => (
            <li key={i}>{v.detail}</li>
          ))}
        </ul>
      )}
      {open && (
        <div className="mt-2 space-y-2">
          <div>
            <div className="mb-0.5 font-medium text-muted-foreground">Prompt</div>
            <pre className="max-h-40 overflow-auto rounded bg-muted/50 p-1.5 leading-snug whitespace-pre-wrap">
              {entry.userPrompt}
            </pre>
          </div>
          <div>
            <div className="mb-0.5 font-medium text-muted-foreground">Raw response</div>
            <pre className="max-h-40 overflow-auto rounded bg-muted/50 p-1.5 leading-snug whitespace-pre-wrap">
              {entry.rawResponse}
            </pre>
          </div>
          {entry.cleanedText !== entry.rawResponse && (
            <div>
              <div className="mb-0.5 font-medium text-muted-foreground">Cleaned</div>
              <pre className="max-h-40 overflow-auto rounded bg-muted/50 p-1.5 leading-snug whitespace-pre-wrap">
                {entry.cleanedText}
              </pre>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
