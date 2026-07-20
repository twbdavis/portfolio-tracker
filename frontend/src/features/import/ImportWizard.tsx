import { useCallback, useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { FileUp, Trash2 } from 'lucide-react'
import { toast } from 'sonner'
import { api, getApiBase, getToken } from '@/lib/api'
import { cn } from '@/lib/utils'
import { shortDate } from '@/lib/format'
import type { TaxTreatment } from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { AddLotDialog } from '@/features/dashboard/LotDialogs'

interface UnknownAccount {
  accountNumber: string
  accountNameRaw: string | null
  suggestedName: string
  suggestedTreatment: TaxTreatment
  evidence: string | null
}

interface Preview {
  previewId: string
  tradeRows: number
  cashRows: number
  skippedKnown: number
  unrecognizedTypes: Record<string, number>
  unknownAccounts: UnknownAccount[]
  adoptedByPlaceholder: string | null
}

interface HeaderMismatch {
  actualHeaders: string[]
  requiredFields: string[]
}

interface Reconciliation {
  accountId: number
  account: string
  ticker: string
  shares: number
  needsOpeningBalance: boolean
}

interface CommitResult {
  rowsImported: number
  rowsSkipped: number
  rowsDeduped: number
  unrecognizedTypes: Record<string, number>
  reconciliation: Reconciliation[]
}

interface Batch {
  id: number
  account: string
  importedAt: string
  fileName: string
  rowsImported: number
  rowsSkipped: number
  rowsDeduped: number
}

type Step =
  | { kind: 'pick' }
  | { kind: 'map'; file: File; mismatch: HeaderMismatch }
  | { kind: 'preview'; preview: Preview; decisions: Record<string, { displayName: string; treatment: TaxTreatment }> }
  | { kind: 'done'; result: CommitResult }

async function uploadPreview(file: File, columnMap?: Record<string, string>): Promise<Preview | { mismatch: HeaderMismatch }> {
  const form = new FormData()
  form.append('file', file)
  if (columnMap) form.append('columnMap', JSON.stringify(columnMap))
  const resp = await fetch(`${getApiBase()}/api/import/preview`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${getToken()}` },
    body: form,
  })
  if (resp.status === 422) return { mismatch: (await resp.json()) as HeaderMismatch }
  if (!resp.ok) {
    const body = await resp.json().catch(() => null)
    throw new Error(body?.error ?? `${resp.status} ${resp.statusText}`)
  }
  return (await resp.json()) as Preview
}

/// The Chase CSV import wizard: drop/browse → (column remap if headers differ)
/// → preview with unknown-account cards → commit → reconciliation summary with
/// the opening-balance follow-up for flagged tickers.
export function ImportWizard({
  open,
  onOpenChange,
}: {
  open: boolean
  onOpenChange: (o: boolean) => void
}) {
  const [step, setStep] = useState<Step>({ kind: 'pick' })
  const [busy, setBusy] = useState(false)
  const [dragOver, setDragOver] = useState(false)
  const queryClient = useQueryClient()

  useEffect(() => {
    if (open) setStep({ kind: 'pick' })
  }, [open])

  const handleFile = useCallback(async (file: File, columnMap?: Record<string, string>) => {
    setBusy(true)
    try {
      const result = await uploadPreview(file, columnMap)
      if ('mismatch' in result) {
        setStep({ kind: 'map', file, mismatch: result.mismatch })
      } else {
        setStep({ kind: 'preview', preview: result, decisions: {} })
      }
    } catch (e) {
      toast.error(String(e))
    } finally {
      setBusy(false)
    }
  }, [])

  const commit = useMutation({
    mutationFn: (p: { previewId: string; decisions: Record<string, { displayName: string; treatment: TaxTreatment }> }) =>
      api.post<CommitResult>('/api/import/commit', {
        previewId: p.previewId,
        newAccounts: Object.entries(p.decisions).map(([accountNumber, d]) => ({
          accountNumber,
          displayName: d.displayName,
          treatment: d.treatment,
        })),
      }),
    onSuccess: (result) => {
      queryClient.invalidateQueries()
      setStep({ kind: 'done', result })
    },
    onError: (e) => toast.error(String(e)),
  })

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-2xl">
        <Tabs defaultValue="import">
          <TabsList>
            <TabsTrigger value="import">Import</TabsTrigger>
            <TabsTrigger value="batches">Batches</TabsTrigger>
          </TabsList>

          <TabsContent value="import">
            {step.kind === 'pick' && (
              <>
                <DialogHeader>
                  <DialogTitle>Import Chase transactions CSV</DialogTitle>
                  <DialogDescription>
                    Nothing is written until you confirm the preview.
                  </DialogDescription>
                </DialogHeader>
                <label
                  onDragOver={(e) => {
                    e.preventDefault()
                    setDragOver(true)
                  }}
                  onDragLeave={() => setDragOver(false)}
                  onDrop={(e) => {
                    e.preventDefault()
                    setDragOver(false)
                    const file = e.dataTransfer.files[0]
                    if (file) handleFile(file)
                  }}
                  className={cn(
                    'mt-3 flex cursor-pointer flex-col items-center justify-center gap-2 rounded-lg border-2 border-dashed py-14 transition-colors',
                    dragOver ? 'border-primary bg-accent/40' : 'border-border hover:border-primary/60',
                  )}
                >
                  <FileUp className="size-8 text-muted-foreground" />
                  <span className="text-sm">
                    {busy ? 'Parsing…' : 'Drop the CSV here, or click to browse'}
                  </span>
                  <input
                    type="file"
                    accept=".csv"
                    className="hidden"
                    onChange={(e) => {
                      const file = e.target.files?.[0]
                      if (file) handleFile(file)
                      e.target.value = ''
                    }}
                  />
                </label>
              </>
            )}

            {step.kind === 'map' && (
              <ColumnMapStep
                mismatch={step.mismatch}
                busy={busy}
                onSubmit={(map) => handleFile(step.file, map)}
                onBack={() => setStep({ kind: 'pick' })}
              />
            )}

            {step.kind === 'preview' && (
              <PreviewStep
                preview={step.preview}
                decisions={step.decisions}
                onDecision={(number, d) =>
                  setStep({ ...step, decisions: { ...step.decisions, [number]: d } })
                }
                onCommit={() =>
                  commit.mutate({ previewId: step.preview.previewId, decisions: step.decisions })
                }
                committing={commit.isPending}
                onBack={() => setStep({ kind: 'pick' })}
              />
            )}

            {step.kind === 'done' && (
              <ResultStep result={step.result} onClose={() => onOpenChange(false)} />
            )}
          </TabsContent>

          <TabsContent value="batches">
            <BatchesPanel />
          </TabsContent>
        </Tabs>
      </DialogContent>
    </Dialog>
  )
}

function ColumnMapStep({
  mismatch,
  busy,
  onSubmit,
  onBack,
}: {
  mismatch: HeaderMismatch
  busy: boolean
  onSubmit: (map: Record<string, string>) => void
  onBack: () => void
}) {
  const [map, setMap] = useState<Record<string, string>>(() => {
    const initial: Record<string, string> = {}
    for (const field of mismatch.requiredFields) {
      // pre-match identical headers case-insensitively
      const hit = mismatch.actualHeaders.find((h) => h.toLowerCase() === field.toLowerCase())
      if (hit) initial[field] = hit
    }
    return initial
  })
  const complete = mismatch.requiredFields.every((f) => map[f])

  return (
    <>
      <DialogHeader>
        <DialogTitle>Match columns</DialogTitle>
        <DialogDescription>
          This file's headers don't match the Chase export. Map each required field to one of the
          file's columns.
        </DialogDescription>
      </DialogHeader>
      <div className="grid max-h-80 grid-cols-2 gap-2 overflow-y-auto py-2">
        {mismatch.requiredFields.map((field) => (
          <div key={field} className="flex items-center gap-2">
            <Label className="w-32 shrink-0 text-xs">{field}</Label>
            <Select
              value={map[field] ?? ''}
              onValueChange={(v) => setMap((m) => ({ ...m, [field]: v }))}
            >
              <SelectTrigger className="h-8 flex-1 text-xs">
                <SelectValue placeholder="—" />
              </SelectTrigger>
              <SelectContent>
                {mismatch.actualHeaders.map((h) => (
                  <SelectItem key={h} value={h}>
                    {h}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        ))}
      </div>
      <DialogFooter>
        <Button variant="outline" onClick={onBack}>
          Back
        </Button>
        <Button disabled={!complete || busy} onClick={() => onSubmit(map)}>
          Continue
        </Button>
      </DialogFooter>
    </>
  )
}

function PreviewStep({
  preview,
  decisions,
  onDecision,
  onCommit,
  committing,
  onBack,
}: {
  preview: Preview
  decisions: Record<string, { displayName: string; treatment: TaxTreatment }>
  onDecision: (number: string, d: { displayName: string; treatment: TaxTreatment }) => void
  onCommit: () => void
  committing: boolean
  onBack: () => void
}) {
  const unrecognized = Object.entries(preview.unrecognizedTypes)
  return (
    <>
      <DialogHeader>
        <DialogTitle>Preview</DialogTitle>
        <DialogDescription>
          {preview.tradeRows} trades and {preview.cashRows} cash flows recognized
          {preview.skippedKnown > 0 && `, ${preview.skippedKnown} known noise rows skipped`}.
          Duplicate rows are detected at commit.
        </DialogDescription>
      </DialogHeader>

      {unrecognized.length > 0 && (
        <p className="text-xs text-warn">
          Unrecognized row types (skipped):{' '}
          {unrecognized.map(([t, n]) => `${t} ×${n}`).join(', ')}
        </p>
      )}

      {preview.adoptedByPlaceholder && (
        <p className="text-xs text-muted-foreground">
          Rows match your pre-existing data — that account adopts number{' '}
          {preview.adoptedByPlaceholder}.
        </p>
      )}

      {preview.unknownAccounts.length > 0 && (
        <div className="space-y-3">
          <p className="text-sm font-medium">
            {preview.unknownAccounts.length === 1
              ? 'This file contains a new account'
              : `This file contains ${preview.unknownAccounts.length} new accounts`}
          </p>
          {preview.unknownAccounts.map((u) => {
            const d = decisions[u.accountNumber] ?? {
              displayName: u.suggestedName,
              treatment: u.suggestedTreatment,
            }
            return (
              <div key={u.accountNumber} className="space-y-2 rounded-md border p-3">
                <div className="flex items-center gap-2 text-sm">
                  <Badge variant="outline">{u.accountNumber}</Badge>
                  {u.accountNameRaw && (
                    <span className="text-muted-foreground">{u.accountNameRaw}</span>
                  )}
                </div>
                {u.evidence && (
                  <p className="text-xs text-muted-foreground">
                    Suggested {u.suggestedTreatment === 'Taxable' ? 'taxable' : 'IRA'}: {u.evidence}
                  </p>
                )}
                <div className="grid grid-cols-2 gap-2">
                  <div className="space-y-1">
                    <Label className="text-xs">Display name</Label>
                    <Input
                      className="h-8"
                      value={d.displayName}
                      onChange={(e) =>
                        onDecision(u.accountNumber, { ...d, displayName: e.target.value })
                      }
                    />
                  </div>
                  <div className="space-y-1">
                    <Label className="text-xs">Tax treatment</Label>
                    <Select
                      value={d.treatment}
                      onValueChange={(v) =>
                        onDecision(u.accountNumber, { ...d, treatment: v as TaxTreatment })
                      }
                    >
                      <SelectTrigger className="h-8">
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value="Taxable">Taxable</SelectItem>
                        <SelectItem value="RothIRA">Roth IRA</SelectItem>
                        <SelectItem value="TraditionalIRA">Traditional IRA</SelectItem>
                      </SelectContent>
                    </Select>
                  </div>
                </div>
              </div>
            )
          })}
        </div>
      )}

      <DialogFooter>
        <Button variant="outline" onClick={onBack}>
          Back
        </Button>
        <Button onClick={onCommit} disabled={committing}>
          {committing ? 'Importing…' : 'Import'}
        </Button>
      </DialogFooter>
    </>
  )
}

function ResultStep({ result, onClose }: { result: CommitResult; onClose: () => void }) {
  const flagged = result.reconciliation.filter((r) => r.needsOpeningBalance)
  const [openingFor, setOpeningFor] = useState<Reconciliation | null>(null)
  return (
    <>
      <DialogHeader>
        <DialogTitle>Import complete</DialogTitle>
        <DialogDescription>
          {result.rowsImported} rows imported, {result.rowsDeduped} duplicates skipped
          {result.rowsSkipped > 0 && `, ${result.rowsSkipped} noise rows ignored`}.
        </DialogDescription>
      </DialogHeader>

      {result.reconciliation.length > 0 && (
        <div className="max-h-64 overflow-y-auto rounded-md border">
          <table className="w-full text-xs">
            <thead>
              <tr className="border-b text-muted-foreground">
                <th className="px-2 py-1.5 text-left">Account</th>
                <th className="px-2 py-1.5 text-left">Ticker</th>
                <th className="px-2 py-1.5 text-right">Net shares</th>
                <th className="px-2 py-1.5"></th>
              </tr>
            </thead>
            <tbody>
              {result.reconciliation.map((r) => (
                <tr key={`${r.accountId}-${r.ticker}`} className="border-b border-border/50 last:border-0">
                  <td className="px-2 py-1">{r.account}</td>
                  <td className="px-2 py-1 font-medium">{r.ticker}</td>
                  <td className="px-2 py-1 text-right tnum">{r.shares}</td>
                  <td className="px-2 py-1 text-right">
                    {r.needsOpeningBalance && (
                      <Button
                        variant="outline"
                        size="sm"
                        className="h-6 border-warn text-xs text-warn"
                        onClick={() => setOpeningFor(r)}
                      >
                        Set opening balance…
                      </Button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {flagged.length > 0 && (
        <p className="text-xs text-warn">
          {flagged.length === 1 ? 'One ticker sold' : `${flagged.length} tickers sold`} more shares
          than the imported history bought — set the opening balance so cost basis is right.
        </p>
      )}

      <DialogFooter>
        <Button onClick={onClose}>Done</Button>
      </DialogFooter>

      <AddLotDialog
        open={openingFor !== null}
        onOpenChange={(o) => !o && setOpeningFor(null)}
        defaultAccountId={openingFor?.accountId ?? null}
        fixedTicker={openingFor?.ticker}
        openingBalanceHint
      />
    </>
  )
}

function BatchesPanel() {
  const queryClient = useQueryClient()
  const query = useQuery({
    queryKey: ['import-batches'],
    queryFn: () => api.get<Batch[]>('/api/import/batches'),
  })
  const del = useMutation({
    mutationFn: (id: number) => api.delete(`/api/import/batches/${id}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['import-batches'] })
      queryClient.invalidateQueries({ queryKey: ['dashboard'] })
      toast.success('Batch deleted and positions rebuilt')
    },
    onError: (e) => toast.error(String(e)),
  })
  const batches = useMemo(() => query.data ?? [], [query.data])

  return (
    <div className="space-y-2">
      <DialogHeader>
        <DialogTitle>Import batches</DialogTitle>
        <DialogDescription>
          Deleting a batch removes its rows and rebuilds the affected positions. Re-importing the
          CSV restores them.
        </DialogDescription>
      </DialogHeader>
      <div className="max-h-80 overflow-y-auto rounded-md border">
        <table className="w-full text-xs">
          <thead>
            <tr className="border-b text-muted-foreground">
              <th className="px-2 py-1.5 text-left">Imported</th>
              <th className="px-2 py-1.5 text-left">File</th>
              <th className="px-2 py-1.5 text-left">Account</th>
              <th className="px-2 py-1.5 text-right">Rows</th>
              <th className="px-2 py-1.5 text-right">Deduped</th>
              <th className="w-10 px-2 py-1.5"></th>
            </tr>
          </thead>
          <tbody>
            {batches.length === 0 && (
              <tr>
                <td colSpan={6} className="px-2 py-4 text-center text-muted-foreground">
                  No imports yet
                </td>
              </tr>
            )}
            {batches.map((b) => (
              <tr key={b.id} className="border-b border-border/50 last:border-0">
                <td className="px-2 py-1 tnum">{shortDate(b.importedAt)}</td>
                <td className="max-w-40 truncate px-2 py-1">{b.fileName}</td>
                <td className="px-2 py-1">{b.account}</td>
                <td className="px-2 py-1 text-right tnum">{b.rowsImported}</td>
                <td className="px-2 py-1 text-right tnum">{b.rowsDeduped}</td>
                <td className="px-2 py-1 text-right">
                  <Button
                    variant="ghost"
                    size="icon"
                    className="size-6 text-destructive"
                    onClick={() => del.mutate(b.id)}
                  >
                    <Trash2 className="size-3.5" />
                  </Button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
