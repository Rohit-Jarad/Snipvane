import { Link, Navigate, Route, Routes } from 'react-router-dom'
import HomePage from './pages/HomePage'
import ReviewPage from './pages/ReviewPage'

export default function App() {
  return (
    <div className="min-h-svh bg-ink text-paper">
      <header className="border-b border-line">
        <div className="mx-auto flex max-w-6xl items-center justify-between px-6 py-4">
          <Link to="/" className="flex items-baseline gap-3 no-underline">
            <span className="font-display text-2xl font-semibold tracking-tight text-paper">
              Snipvane
            </span>
            <span className="hidden text-xs uppercase tracking-[0.22em] text-paper-dim sm:inline">
              Long to short
            </span>
          </Link>
          <p className="text-xs text-paper-dim">Manual review. No auto-publish.</p>
        </div>
      </header>
      <main className="mx-auto max-w-6xl px-6 py-8">
        <Routes>
          <Route path="/" element={<HomePage />} />
          <Route path="/videos/:id" element={<ReviewPage />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
    </div>
  )
}
