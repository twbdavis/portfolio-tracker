import { useEffect, useState } from 'react'
import { useLocation, useNavigate, useSearchParams } from 'react-router-dom'
import { ArrowLeft } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useQuery } from '@tanstack/react-query'
import { api } from '@/lib/api'
import type { Discovery } from '@/lib/types'
import { Badge } from '@/components/ui/badge'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { ResearchTab } from '@/features/research/ResearchTab'
import { WatchlistTab } from '@/features/research/WatchlistTab'
import { DiscoveriesTab } from '@/features/research/DiscoveriesTab'

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
  // set when a ticker was opened from another tab on THIS page (Discoveries)
  // so the back button returns to that tab; clearing the ticker param lets
  // the app-level scroll memory restore the exact list position
  const [backTab, setBackTab] = useState<{ tab: string; label: string } | null>(null)

  // "Research this ticker" links always land on the research tab
  useEffect(() => {
    if (ticker) setTab('research')
  }, [ticker])

  const openTicker = (t: string) => {
    setParams({ ticker: t.trim().toUpperCase() })
    setTab('research')
  }

  const openFrom = (tabName: string, label: string) => (t: string) => {
    setBackTab({ tab: tabName, label })
    openTicker(t)
  }

  const changeTab = (t: string) => {
    setTab(t)
    // manually leaving the research tab ends the detour — a stale "Back to
    // Discoveries" after the user navigated themselves is just confusing
    if (t !== 'research') setBackTab(null)
  }

  const returnToBackTab = () => {
    if (!backTab) return
    setParams({})
    setTab(backTab.tab)
    setBackTab(null)
  }

  // badge count for the peer-discovery inbox
  const discoveries = useQuery({
    queryKey: ['discoveries'],
    queryFn: () => api.get<Discovery[]>('/api/discoveries'),
  })
  const discoveryCount = discoveries.data?.length ?? 0

  return (
    <Tabs value={tab} onValueChange={changeTab}>
      <div className="flex items-center gap-3 border-b px-4 pt-3 pb-2">
        {backTab && tab === 'research' ? (
          <Button variant="outline" size="sm" onClick={returnToBackTab}>
            <ArrowLeft className="size-4" /> Back to {backTab.label}
          </Button>
        ) : (
          back && (
            <Button variant="outline" size="sm" onClick={() => navigate(back.path)}>
              <ArrowLeft className="size-4" /> Back to {back.label}
            </Button>
          )
        )}
        <TabsList>
          <TabsTrigger value="research">Research</TabsTrigger>
          <TabsTrigger value="watchlist">Watchlist</TabsTrigger>
          <TabsTrigger value="discoveries">
            Discoveries
            {discoveryCount > 0 && (
              <Badge variant="secondary" className="ml-1.5 px-1.5 py-0 text-[10px] tnum">
                {discoveryCount}
              </Badge>
            )}
          </TabsTrigger>
        </TabsList>
      </div>
      <TabsContent value="research">
        <ResearchTab ticker={ticker} onOpenTicker={openTicker} />
      </TabsContent>
      <TabsContent value="watchlist">
        <WatchlistTab onOpenTicker={openFrom('watchlist', 'Watchlist')} />
      </TabsContent>
      <TabsContent value="discoveries">
        <DiscoveriesTab onOpenTicker={openFrom('discoveries', 'Discoveries')} />
      </TabsContent>
    </Tabs>
  )
}
