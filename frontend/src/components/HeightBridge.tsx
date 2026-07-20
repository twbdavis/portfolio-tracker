import { useEffect } from 'react'

declare global {
  interface Window {
    chrome?: { webview?: { postMessage: (msg: unknown) => void } }
  }
}

/// WebView2 bridge: after the dashboard renders, report the ideal content
/// height so the shell can grow the window to fit (grow-only, clamped there).
export function HeightBridge() {
  useEffect(() => {
    const webview = window.chrome?.webview
    if (!webview) return
    let last = 0
    const report = () => {
      // the layout is pinned to the viewport (totals strip at the bottom), so
      // the desired height is the viewport plus however much the internal
      // scroll regions (the positions grid) are overflowing
      const overflow = Array.from(document.querySelectorAll('[data-grow-region]'))
        .reduce((sum, el) => sum + Math.max(0, el.scrollHeight - el.clientHeight), 0)
      const height = Math.ceil(document.documentElement.scrollHeight + overflow)
      if (height !== last) {
        last = height
        webview.postMessage({ type: 'contentHeight', height })
      }
    }
    report()
    const observer = new ResizeObserver(report)
    observer.observe(document.body)
    return () => observer.disconnect()
  }, [])
  return null
}
