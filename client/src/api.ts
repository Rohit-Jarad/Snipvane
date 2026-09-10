import type { Clip, HealthStatus, UploadResponse, VideoDetail, VideoListItem } from './types'

async function parseError(response: Response): Promise<string> {
  const text = await response.text()
  try {
    const json = JSON.parse(text) as { message?: string; title?: string }
    return json.message || json.title || text || response.statusText
  } catch {
    return text || response.statusText
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, init)
  if (!response.ok) {
    throw new Error(await parseError(response))
  }
  if (response.status === 204) {
    return undefined as T
  }
  return (await response.json()) as T
}

export const api = {
  health: () => request<HealthStatus>('/api/health'),

  listVideos: () => request<VideoListItem[]>('/api/videos'),

  getVideo: (id: string) => request<VideoDetail>(`/api/videos/${id}`),

  uploadVideo: async (file: File, onProgress?: (pct: number) => void) => {
    return new Promise<UploadResponse>((resolve, reject) => {
      const xhr = new XMLHttpRequest()
      xhr.open('POST', '/api/videos/upload')
      xhr.upload.onprogress = (event) => {
        if (event.lengthComputable && onProgress) {
          onProgress(Math.round((event.loaded / event.total) * 100))
        }
      }
      xhr.onload = () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          resolve(JSON.parse(xhr.responseText) as UploadResponse)
        } else {
          reject(new Error(xhr.responseText || xhr.statusText))
        }
      }
      xhr.onerror = () => reject(new Error('Upload failed'))
      const form = new FormData()
      form.append('file', file)
      xhr.send(form)
    })
  },

  reprocess: (id: string) =>
    request<{ id: string; status: string }>(`/api/videos/${id}/process`, {
      method: 'POST',
    }),

  updateClip: (id: string, body: { isKept?: boolean; sortOrder?: number }) =>
    request<Clip>(`/api/clips/${id}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }),

  reorderClips: (clipIds: string[]) =>
    request<void>('/api/clips/reorder', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ clipIds }),
    }),

  clipPreviewUrl: (id: string) => `/api/clips/${id}/file`,
  clipDownloadUrl: (id: string) => `/api/clips/${id}/download`,
  sourceVideoUrl: (id: string) => `/api/videos/${id}/file`,
}
