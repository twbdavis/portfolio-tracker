import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { ResearchTab } from '@/features/research/ResearchTab'
import { WatchlistTab } from '@/features/research/WatchlistTab'

/// The ResearchForm: a Research tab (deep dive on one ticker + compare tray)
/// and the Watchlist tab. Dashboard rows deep-link here via ?ticker=.
export function ResearchPage() {
  const [params, setParams] = useSearchParams()
  const ticker = params.get('ticker')?.trim().toUpperCase() || null
  const [tab, setTab] = useState('research')

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
      <div className="border-b px-4 pt-3 pb-2">
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
