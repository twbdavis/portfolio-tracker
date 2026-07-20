import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router-dom'
import { initSession } from './lib/api'
import { LiveProvider } from './lib/signalr'
import { ThemeProvider } from './lib/theme'
import { TooltipProvider } from './components/ui/tooltip'
import { Toaster } from './components/ui/sonner'
import App from './App'
import './index.css'

initSession()
// apply the persisted theme before first paint to avoid a light-mode flash
document.documentElement.classList.toggle(
  'dark',
  (localStorage.getItem('pt-theme') ?? 'dark') === 'dark',
)

const queryClient = new QueryClient({
  defaultOptions: {
    queries: { staleTime: 15_000, retry: 1, refetchOnWindowFocus: false },
  },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ThemeProvider>
      <QueryClientProvider client={queryClient}>
        <LiveProvider>
          <TooltipProvider delayDuration={300}>
            <BrowserRouter>
              <App />
            </BrowserRouter>
          </TooltipProvider>
          <Toaster position="bottom-right" />
        </LiveProvider>
      </QueryClientProvider>
    </ThemeProvider>
  </StrictMode>,
)
