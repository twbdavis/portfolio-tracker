import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { api } from '@/lib/api'
import { money } from '@/lib/format'
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

/// Manual baseline for the Daily Gain/Loss total — only honored today, only on
/// the cumulative view (the WinForms DayStartForm).
export function DayStartDialog({
  open,
  onOpenChange,
  marketValue,
  prevCloseBaseline,
  isCustom,
}: {
  open: boolean
  onOpenChange: (o: boolean) => void
  marketValue: number
  prevCloseBaseline: number | null
  isCustom: boolean
}) {
  const [value, setValue] = useState('')
  const queryClient = useQueryClient()

  useEffect(() => {
    if (open) setValue('')
  }, [open])

  const save = useMutation({
    mutationFn: (v: number | null) => api.put('/api/settings/daystart', { value: v }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['dashboard'] })
      onOpenChange(false)
    },
    onError: (e) => toast.error(String(e)),
  })

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Today's starting value</DialogTitle>
          <DialogDescription>
            Daily Gain/Loss is measured from the previous close
            {prevCloseBaseline != null && ` (${money(prevCloseBaseline)})`}. Set a custom baseline
            for today if you added or withdrew money. Current market value: {money(marketValue)}.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-2">
          <Label htmlFor="day-start">Custom starting value</Label>
          <Input
            id="day-start"
            type="number"
            step="0.01"
            placeholder={prevCloseBaseline != null ? String(prevCloseBaseline.toFixed(2)) : ''}
            value={value}
            onChange={(e) => setValue(e.target.value)}
          />
        </div>
        <DialogFooter>
          {isCustom && (
            <Button variant="outline" onClick={() => save.mutate(null)}>
              Clear custom baseline
            </Button>
          )}
          <Button
            onClick={() => {
              const v = parseFloat(value)
              if (Number.isNaN(v)) return
              save.mutate(v)
            }}
            disabled={save.isPending || value === ''}
          >
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
