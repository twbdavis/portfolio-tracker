import { useEffect, useState } from 'react'
import { useLocation, useNavigate, useSearchParams } from 'react-router-dom'
import { ArrowLeft } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { ResearchTab } from '@/features/research/ResearchTab'
import { WatchlistTab } from '@/features/research/WatchlistTab'

/// Where to return after a research detour; Sectors/Signals set this via
/// router state when a row click deep-links here.
interface BackRef {
  path: string
  label: string
}

/// The ResearchForm: a Research tab (deep dive on one ticker + compare tray)
/// and the Watchlist tab. Dashboard rows deep-link here via ?ticker=.
export function ResearchPage() {
  const [params, setParams] = useSearchParams()
  const location = useLocation()
  const navigate = useNavigate()
  const ticker = params.get('ticker')?.trim().toUpperCase() || null
  const [tab, setTab] = useState('research')
  // captured once on mount: switching tickers replaces the history entry's
  // state, but the return target stays valid for the whole research detour
  const [back] = useState<BackRef | null>(
    () => (location.state as { back?: BackRef } | null)?.back ?? null,
  )

  // "Research this ticker" links always land on the research tab
  useEffect(() => {
    if (ticker) setTab('research')
  }, [ticker])

  const openTicker = (t: string) => {
    setParams({ ticker: t.trim().toUpperCase() })
    setTab('research')
  }

  return (
    <Tabs value={tab} onValueChange={setTab}>
      <div className="flex items-center gap-3 border-b px-4 pt-3 pb-2">
        {back && (
          <Button variant="outline" size="sm" onClick={() => navigate(back.path)}>
            <ArrowLeft className="size-4" /> Back to {back.label}
          </Button>
        )}
        <TabsList>
          <TabsTrigger value="research">Research</TabsTrigger>
          <TabsTrigger value="watchlist">Watchlist</TabsTrigger>
        </TabsList>
      </div>
      <TabsContent value="research">
        <ResearchTab ticker={ticker} onOpenTicker={openTicker} />
      </TabsContent>
      <TabsContent value="watchlist">
        <WatchlistTab onOpenTicker={openTicker} />
      </TabsContent>
    </Tabs>
  )
}
