import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { api } from '@/lib/api'
import type { Account, TaxTreatment } from '@/lib/types'
import { useAccounts } from '@/hooks/useAccounts'
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

/// First launch after the multi-account migration: existing data landed in a
/// placeholder account — ask once for its number, name, and tax treatment.
/// Any PATCH clears needsReview server-side, so this shows exactly once.
export function MigrationReviewDialog() {
  const { data: accounts } = useAccounts()
  const queryClient = useQueryClient()
  const pending = accounts?.filter((a) => a.needsReview) ?? []
  const account: Account | undefined = pending[0]

  const [number, setNumber] = useState('')
  const [name, setName] = useState('')
  const [treatment, setTreatment] = useState<TaxTreatment>('Taxable')

  useEffect(() => {
    if (account) {
      setNumber(account.accountNumber)
      setName(account.displayName)
      setTreatment(account.taxTreatment)
    }
  }, [account?.id]) // eslint-disable-line react-hooks/exhaustive-deps

  const save = useMutation({
    mutationFn: () =>
      api.patch(`/api/accounts/${account!.id}`, {
        accountNumber: number.trim(),
        displayName: name.trim() || account!.displayName,
        taxTreatment: treatment,
      }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['accounts'] }),
    onError: (e) => toast.error(String(e)),
  })

  // dismissing still clears the flag — the WinForms prompt asked only once
  const dismiss = useMutation({
    mutationFn: () => api.patch(`/api/accounts/${account!.id}`, {}),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['accounts'] }),
  })

  if (!account) return null

  return (
    <Dialog open onOpenChange={(o) => !o && dismiss.mutate()}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Describe your existing account</DialogTitle>
          <DialogDescription>
            Your existing data was migrated into "{account.displayName}". Set its account number
            (as it appears in Chase exports, e.g. ...1234) and tax treatment so imports and tax
            math route correctly.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          <div className="space-y-1.5">
            <Label htmlFor="mig-name">Display name</Label>
            <Input id="mig-name" value={name} onChange={(e) => setName(e.target.value)} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="mig-number">Account number</Label>
            <Input
              id="mig-number"
              placeholder="...1234"
              value={number}
              onChange={(e) => setNumber(e.target.value)}
            />
          </div>
          <div className="space-y-1.5">
            <Label>Tax treatment</Label>
            <Select value={treatment} onValueChange={(v) => setTreatment(v as TaxTreatment)}>
              <SelectTrigger>
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
        <DialogFooter>
          <Button variant="outline" onClick={() => dismiss.mutate()}>
            Skip
          </Button>
          <Button onClick={() => save.mutate()} disabled={save.isPending}>
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
