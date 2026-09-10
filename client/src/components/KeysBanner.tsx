import { useEffect, useState } from 'react'
import { api } from '../api'
import type { HealthStatus } from '../types'

export default function KeysBanner() {
  const [health, setHealth] = useState<HealthStatus | null>(null)

  useEffect(() => {
    void api.health().then(setHealth).catch(() => setHealth(null))
  }, [])

  if (!health || (health.whisperConfigured && health.highlightsConfigured)) {
    return null
  }

  return (
    <div className="border border-copper/40 bg-ink-2 px-4 py-3 text-sm text-paper">
      <p className="font-medium text-copper">Gemini API key required (free tier)</p>
      <p className="mt-1 text-paper-dim">
        Add <code className="text-paper">Ai:GeminiApiKey</code> in{' '}
        <code className="text-paper">Snipvane/appsettings.Local.json</code>, save, then click Run
        again. OpenAI is optional and paid — transcription now uses Gemini.
      </p>
    </div>
  )
}
