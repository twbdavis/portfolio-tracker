import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { HubConnectionBuilder, HubConnection, LogLevel } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { getApiBase, getToken } from './api'
import { applyQuotesToDashboard, applyQuotesToWatchlist } from './quotes'
import type { AlertPush, Dashboard, QuotePush, WatchlistItem } from './types'

interface LiveContextValue {
  connected: boolean
  /** fetchedAt-stamped last change direction per ticker, for cell flashes */
  flashes: Map<string, 'up' | 'down'>
}

const LiveContext = createContext<LiveContextValue>({ connected: false, flashes: new Map() })

export function useLive() {
  return useContext(LiveContext)
}

/// One SignalR connection for the whole app. Quote waves are patched straight
/// into the cached dashboard/watchlist data (prices on screen with no
/// round-trip); one debounced refetch after the last wave reconciles the
/// authoritative server numbers. alertFired mirrors the Windows toast in-app.
export function LiveProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const [connected, setConnected] = useState(false)
  const [flashes, setFlashes] = useState<Map<string, 'up' | 'down'>>(new Map())
  const lastPrices = useRef<Map<string, number>>(new Map())
  const reconcile = useRef<ReturnType<typeof setTimeout> | null>(null)

  useEffect(() => {
    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl(`${getApiBase()}/hub`, { accessTokenFactory: () => getToken() })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('quotes', (batch: QuotePush[]) => {
      const next = new Map<string, 'up' | 'down'>()
      for (const q of batch) {
        const prev = lastPrices.current.get(q.ticker)
        if (prev !== undefined && q.price !== prev) next.set(q.ticker, q.price > prev ? 'up' : 'down')
        lastPrices.current.set(q.ticker, q.price)
      }
      if (next.size > 0) {
        // merge so an earlier wave's flash isn't cut short by the next wave
        setFlashes((prev) => new Map([...prev, ...next]))
        setTimeout(
          () =>
            setFlashes((prev) => {
              const m = new Map(prev)
              for (const k of next.keys()) m.delete(k)
              return m
            }),
          1400, // let the CSS animation finish
        )
      }

      queryClient.setQueriesData<Dashboard>({ queryKey: ['dashboard'] }, (old) =>
        old ? applyQuotesToDashboard(old, batch) : old,
      )
      queryClient.setQueriesData<WatchlistItem[]>({ queryKey: ['watchlist'] }, (old) =>
        old ? applyQuotesToWatchlist(old, batch) : old,
      )
      // one authoritative refetch per refresh pass, after the waves stop
      if (reconcile.current) clearTimeout(reconcile.current)
      reconcile.current = setTimeout(() => {
        queryClient.invalidateQueries({ queryKey: ['dashboard'] })
        queryClient.invalidateQueries({ queryKey: ['watchlist'] })
      }, 1500)
    })

    connection.on('alertFired', (alert: AlertPush) => {
      // no invalidation: the quote wave that triggered the alert already
      // patched the caches, and the pass's reconcile refetch is pending
      toast.warning(alert.message, { duration: 10000 })
    })

    connection.on('importProgress', () => {
      queryClient.invalidateQueries({ queryKey: ['import-batches'] })
    })

    connection
      .start()
      .then(() => setConnected(true))
      .catch(() => setConnected(false))
    connection.onreconnected(() => setConnected(true))
    connection.onclose(() => setConnected(false))

    return () => {
      if (reconcile.current) clearTimeout(reconcile.current)
      connection.stop()
    }
  }, [queryClient])

  return <LiveContext.Provider value={{ connected, flashes }}>{children}</LiveContext.Provider>
}
