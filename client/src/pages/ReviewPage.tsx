import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api } from '../api'
import KeysBanner from '../components/KeysBanner'
import type { Clip, VideoDetail, VideoStatus } from '../types'

const ACTIVE: VideoStatus[] = [
  'Uploaded',
  'ExtractingAudio',
  'Transcribing',
  'Analyzing',
  'GeneratingClips',
]

const STAGE_COPY: Record<VideoStatus, string> = {
  Uploaded: 'Queued',
  ExtractingAudio: 'Extracting audio with FFmpeg',
  Transcribing: 'Whisper word-level transcript',
  Analyzing: 'Scoring highlight moments',
  GeneratingClips: 'Cutting vertical clips + burning captions (can take a few minutes)',
  Completed: 'Ready for review',
  Failed: 'Pipeline failed',
}

function formatClock(seconds: number) {
  const m = Math.floor(seconds / 60)
  const s = Math.floor(seconds % 60)
  return `${m}:${s.toString().padStart(2, '0')}`
}

export default function ReviewPage() {
  const { id } = useParams<{ id: string }>()
  const [video, setVideo] = useState<VideoDetail | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busyId, setBusyId] = useState<string | null>(null)

  const load = useCallback(async () => {
    if (!id) return
    try {
      setVideo(await api.getVideo(id))
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not load video')
    }
  }, [id])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    if (!video || !ACTIVE.includes(video.status)) return
    const timer = window.setInterval(() => void load(), 2500)
    return () => window.clearInterval(timer)
  }, [video, load])

  const clips = video?.clips ?? []
  const keptCount = clips.filter((c) => c.isKept).length

  async function toggleKept(clip: Clip) {
    setBusyId(clip.id)
    try {
      await api.updateClip(clip.id, { isKept: !clip.isKept })
      await load()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Update failed')
    } finally {
      setBusyId(null)
    }
  }

  async function move(clip: Clip, direction: -1 | 1) {
    if (!video) return
    const ordered = [...video.clips].sort((a, b) => a.sortOrder - b.sortOrder)
    const index = ordered.findIndex((c) => c.id === clip.id)
    const next = index + direction
    if (index < 0 || next < 0 || next >= ordered.length) return
    const swapped = [...ordered]
    ;[swapped[index], swapped[next]] = [swapped[next], swapped[index]]
    setBusyId(clip.id)
    try {
      await api.reorderClips(swapped.map((c) => c.id))
      await load()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Reorder failed')
    } finally {
      setBusyId(null)
    }
  }

  async function rerun() {
    if (!id) return
    setBusyId('rerun')
    try {
      await api.reprocess(id)
      await load()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not requeue')
    } finally {
      setBusyId(null)
    }
  }

  if (error && !video) {
    return (
      <div>
        <p className="text-sm text-bad">{error}</p>
        <Link to="/" className="mt-4 inline-block text-sm text-paper-dim">
          Back
        </Link>
      </div>
    )
  }

  if (!video) {
    return <p className="text-sm text-paper-dim">Loading…</p>
  }

  return (
    <div className="space-y-8">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <Link to="/" className="text-xs uppercase tracking-[0.2em] text-paper-dim no-underline hover:text-paper">
            All jobs
          </Link>
          <h1 className="mt-2 font-display text-3xl text-paper">{video.originalFileName}</h1>
          <p className="mt-1 text-sm text-paper-dim">{STAGE_COPY[video.status]}</p>
        </div>
        <div className="flex items-center gap-3">
          <span className="text-xs uppercase tracking-[0.16em] text-paper-dim">
            {keptCount} kept / {clips.length} generated
          </span>
          {(video.status === 'Failed' || video.status === 'Completed') && (
            <button
              type="button"
              onClick={() => void rerun()}
              disabled={busyId === 'rerun'}
              className="border border-line px-3 py-2 text-xs uppercase tracking-[0.16em] text-paper hover:border-copper"
            >
              {busyId === 'rerun' ? 'Queueing…' : 'Run again'}
            </button>
          )}
        </div>
      </div>

      {video.status === 'Failed' && video.errorMessage && (
        <p className="border border-bad/40 bg-ink-2 px-4 py-3 text-sm text-bad">{video.errorMessage}</p>
      )}
      {video.status === 'Failed' && <KeysBanner />}
      {error && <p className="text-sm text-bad">{error}</p>}

      <div className="grid gap-8 lg:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)]">
        <section className="space-y-4">
          <h2 className="font-display text-lg text-paper">Source</h2>
          <video
            className="w-full border border-line bg-black"
            src={api.sourceVideoUrl(video.id)}
            controls
          />
          <TranscriptPanel video={video} />
        </section>

        <section className="space-y-4">
          <h2 className="font-display text-lg text-paper">Clip candidates</h2>
          {clips.length === 0 ? (
            <p className="border border-line bg-ink-2 px-4 py-8 text-sm text-paper-dim">
              {ACTIVE.includes(video.status)
                ? 'Clips will appear here when generation finishes.'
                : 'No clips yet.'}
            </p>
          ) : (
            <ul className="grid gap-5 sm:grid-cols-2">
              {clips.map((clip, index) => (
                <li
                  key={clip.id}
                  className={`border bg-ink-2 ${clip.isKept ? 'border-line' : 'border-line opacity-55'}`}
                >
                  {clip.hasFile ? (
                    <video
                      className="aspect-[9/16] w-full bg-black object-cover"
                      src={api.clipPreviewUrl(clip.id)}
                      controls
                      playsInline
                    />
                  ) : (
                    <div className="flex aspect-[9/16] items-center justify-center text-xs text-paper-dim">
                      File missing
                    </div>
                  )}
                  <div className="space-y-3 p-4">
                    <div className="flex items-start justify-between gap-3">
                      <h3 className="font-display text-base leading-snug text-paper">{clip.title}</h3>
                      <span className="shrink-0 font-display text-2xl text-copper">
                        {clip.viralityScore.toFixed(0)}
                        <span className="block text-center text-[10px] uppercase tracking-[0.14em] text-paper-dim">
                          score
                        </span>
                      </span>
                    </div>
                    <p className="text-xs text-paper-dim">
                      {formatClock(clip.startTime)}–{formatClock(clip.endTime)} · {clip.reason}
                    </p>
                    <div className="flex flex-wrap gap-2">
                      <button
                        type="button"
                        disabled={busyId === clip.id}
                        onClick={() => void toggleKept(clip)}
                        className="border border-line px-2 py-1 text-[11px] uppercase tracking-[0.14em] hover:border-copper"
                      >
                        {clip.isKept ? 'Keep' : 'Dropped'}
                      </button>
                      <button
                        type="button"
                        disabled={index === 0 || busyId === clip.id}
                        onClick={() => void move(clip, -1)}
                        className="border border-line px-2 py-1 text-[11px] uppercase tracking-[0.14em] disabled:opacity-30"
                      >
                        Up
                      </button>
                      <button
                        type="button"
                        disabled={index === clips.length - 1 || busyId === clip.id}
                        onClick={() => void move(clip, 1)}
                        className="border border-line px-2 py-1 text-[11px] uppercase tracking-[0.14em] disabled:opacity-30"
                      >
                        Down
                      </button>
                      {clip.hasFile && (
                        <a
                          href={api.clipDownloadUrl(clip.id)}
                          className="ml-auto border border-copper px-2 py-1 text-[11px] uppercase tracking-[0.14em] text-copper no-underline"
                        >
                          Download
                        </a>
                      )}
                    </div>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>
    </div>
  )
}

function TranscriptPanel({ video }: { video: VideoDetail }) {
  const words = video.transcript?.words ?? []
  const preview = useMemo(() => {
    if (video.transcript?.text) return video.transcript.text
    if (words.length) return words.map((w) => w.word).join(' ')
    return ''
  }, [video.transcript, words])

  return (
    <div className="border border-line bg-ink-2 p-4">
      <div className="mb-2 flex items-center justify-between">
        <h3 className="text-xs uppercase tracking-[0.18em] text-paper-dim">Transcript</h3>
        {video.transcript?.language && (
          <span className="text-[10px] uppercase tracking-[0.16em] text-paper-dim">
            {video.transcript.language}
            {words.length ? ` · ${words.length} words` : ''}
          </span>
        )}
      </div>
      {preview ? (
        <p className="max-h-80 overflow-auto text-sm leading-6 text-paper-dim whitespace-pre-wrap">
          {preview}
        </p>
      ) : (
        <p className="text-sm text-paper-dim">
          {ACTIVE.includes(video.status) ? 'Waiting for Whisper…' : 'No transcript stored.'}
        </p>
      )}
    </div>
  )
}
