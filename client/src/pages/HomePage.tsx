import { useCallback, useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../api'
import KeysBanner from '../components/KeysBanner'
import type { VideoListItem, VideoStatus } from '../types'

const STATUS_LABEL: Record<VideoStatus, string> = {
  Uploaded: 'Uploaded',
  ExtractingAudio: 'Audio',
  Transcribing: 'Transcript',
  Analyzing: 'Highlights',
  GeneratingClips: 'Clips',
  Completed: 'Ready',
  Failed: 'Failed',
}

function statusClass(status: VideoStatus) {
  if (status === 'Completed') return 'text-ok border-ok/40'
  if (status === 'Failed') return 'text-bad border-bad/40'
  return 'text-copper border-copper/40'
}

function formatDuration(seconds: number | null) {
  if (!seconds) return '—'
  const m = Math.floor(seconds / 60)
  const s = Math.round(seconds % 60)
  return `${m}:${s.toString().padStart(2, '0')}`
}

export default function HomePage() {
  const navigate = useNavigate()
  const [videos, setVideos] = useState<VideoListItem[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [progress, setProgress] = useState<number | null>(null)
  const [dragOver, setDragOver] = useState(false)

  const load = useCallback(async () => {
    try {
      setVideos(await api.listVideos())
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not load videos')
    }
  }, [])

  useEffect(() => {
    void load()
    const id = window.setInterval(() => void load(), 4000)
    return () => window.clearInterval(id)
  }, [load])

  async function handleFile(file: File | undefined) {
    if (!file) return
    if (!file.name.toLowerCase().endsWith('.mp4')) {
      setError('Upload an .mp4 file.')
      return
    }
    setError(null)
    setBusy(true)
    setProgress(0)
    try {
      const uploaded = await api.uploadVideo(file, setProgress)
      await load()
      navigate(`/videos/${uploaded.id}`)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Upload failed')
    } finally {
      setBusy(false)
      setProgress(null)
    }
  }

  return (
    <div className="space-y-8">
      <KeysBanner />
      <div className="grid gap-10 lg:grid-cols-[minmax(0,1.1fr)_minmax(0,0.9fr)]">
      <section>
        <p className="text-xs uppercase tracking-[0.28em] text-copper">Ingest</p>
        <h1 className="mt-2 font-display text-4xl font-medium tracking-tight text-paper sm:text-5xl">
          Cut a long video into ranked vertical shorts.
        </h1>
        <p className="mt-4 max-w-xl text-sm leading-6 text-paper-dim">
          Upload an mp4. Long videos are split into audio chunks and transcribed
          on Gemini’s free tier, then the best 30–60 second moments become 9:16
          clips with karaoke captions.
        </p>

        <label
          onDragOver={(e) => {
            e.preventDefault()
            setDragOver(true)
          }}
          onDragLeave={() => setDragOver(false)}
          onDrop={(e) => {
            e.preventDefault()
            setDragOver(false)
            void handleFile(e.dataTransfer.files[0])
          }}
          className={`mt-8 flex min-h-52 cursor-pointer flex-col items-center justify-center border border-dashed px-6 py-10 text-center transition-colors ${
            dragOver ? 'border-copper bg-ink-2' : 'border-line bg-ink-2/60'
          }`}
        >
          <input
            type="file"
            accept="video/mp4,.mp4"
            className="sr-only"
            disabled={busy}
            onChange={(e) => void handleFile(e.target.files?.[0])}
          />
          <span className="font-display text-xl text-paper">Drop an mp4 here</span>
          <span className="mt-2 text-sm text-paper-dim">or click to browse · max 2 GB · one job at a time</span>
          {busy && (
            <span className="mt-4 text-sm text-copper">
              Uploading{progress != null ? ` ${progress}%` : '…'}
            </span>
          )}
        </label>
        {error && <p className="mt-3 text-sm text-bad">{error}</p>}
      </section>

      <section>
        <div className="mb-4 flex items-end justify-between">
          <h2 className="font-display text-xl text-paper">Jobs</h2>
          <button
            type="button"
            onClick={() => void load()}
            className="text-xs uppercase tracking-[0.18em] text-paper-dim hover:text-paper"
          >
            Refresh
          </button>
        </div>
        {videos.length === 0 ? (
          <p className="border border-line bg-ink-2 px-4 py-8 text-sm text-paper-dim">
            No videos yet. The first upload starts the pipeline automatically.
          </p>
        ) : (
          <ul className="divide-y divide-line border border-line">
            {videos.map((video) => (
              <li key={video.id}>
                <Link
                  to={`/videos/${video.id}`}
                  className="flex items-center justify-between gap-4 px-4 py-3 no-underline hover:bg-ink-2"
                >
                  <div className="min-w-0">
                    <p className="truncate text-sm text-paper">{video.originalFileName}</p>
                    <p className="mt-1 text-xs text-paper-dim">
                      {formatDuration(video.durationSeconds)} · {video.clipCount} clips
                    </p>
                  </div>
                  <span
                    className={`shrink-0 border px-2 py-1 text-[10px] uppercase tracking-[0.16em] ${statusClass(video.status)}`}
                  >
                    {STATUS_LABEL[video.status]}
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </section>
      </div>
    </div>
  )
}
