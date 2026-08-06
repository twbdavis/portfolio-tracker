import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'
import type { SymbolSearchResponse } from '@/lib/types'
import {
  Command,
  CommandEmpty,
  CommandInput,
  CommandItem,
  CommandList,
} from '@/components/ui/command'

/// Debounced Finnhub symbol search. Shared by the Research tab (deep dive)
/// and the Queue page (brief any ticker).
export function SearchBox({
  onPick,
  placeholder = 'Ticker or company name',
  className = 'max-w-xl rounded-md border',
  floating = false,
}: {
  onPick: (ticker: string) => void
  placeholder?: string
  className?: string
  /// Render results as an overlay dropdown instead of expanding in-flow —
  /// for tight spots like page headers.
  floating?: boolean
}) {
  const [text, setText] = useState('')
  const [debounced, setDebounced] = useState('')

  useEffect(() => {
    const t = setTimeout(() => setDebounced(text.trim()), 300)
    return () => clearTimeout(t)
  }, [text])

  const search = useQuery({
    queryKey: ['research-search', debounced],
    queryFn: ({ signal }) =>
      api.get<SymbolSearchResponse>(
        `/api/research/search?q=${encodeURIComponent(debounced)}`,
        signal,
      ),
    enabled: debounced.length > 0,
    staleTime: 5 * 60_000, // backspacing to a previous query re-hits the cache instantly
    placeholderData: (prev) => prev, // keep the old list visible while the new one loads
  })

  const pick = (symbol: string) => {
    setText('')
    setDebounced('')
    onPick(symbol)
  }

  const results = debounced.length > 0 && (
    <CommandList>
      {search.data?.error && <div className="px-3 py-2 text-sm text-warn">{search.data.error}</div>}
      {search.isFetching && !search.data && (
        <div className="px-3 py-2 text-sm text-muted-foreground">Searching…</div>
      )}
      {search.data && !search.data.error && <CommandEmpty>No matches.</CommandEmpty>}
      {search.data?.results.slice(0, 20).map((r) => (
        <CommandItem key={r.symbol} value={r.symbol} onSelect={() => pick(r.symbol)}>
          <span className="font-semibold">{r.symbol}</span>
          <span className="truncate text-muted-foreground">{r.description}</span>
          <span className="ml-auto text-xs text-muted-foreground">{r.type}</span>
        </CommandItem>
      ))}
    </CommandList>
  )

  return (
    <Command shouldFilter={false} className={cn('overflow-visible', className)}>
      <CommandInput placeholder={placeholder} value={text} onValueChange={setText} />
      {floating
        ? results && (
            <div className="relative">
              <div className="absolute top-1 right-0 left-0 z-30 max-h-72 overflow-y-auto rounded-md border bg-popover shadow-md">
                {results}
              </div>
            </div>
          )
        : results}
    </Command>
  )
}
