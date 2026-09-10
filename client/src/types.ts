export type VideoStatus =
  | 'Uploaded'
  | 'ExtractingAudio'
  | 'Transcribing'
  | 'Analyzing'
  | 'GeneratingClips'
  | 'Completed'
  | 'Failed'

export type VideoListItem = {
  id: string
  originalFileName: string
  status: VideoStatus
  durationSeconds: number | null
  clipCount: number
  createdAt: string
  errorMessage: string | null
}

export type TranscriptWord = {
  word: string
  start: number
  end: number
}

export type Transcript = {
  text: string
  language: string | null
  words: TranscriptWord[]
}

export type Clip = {
  id: string
  title: string
  startTime: number
  endTime: number
  viralityScore: number
  reason: string
  sortOrder: number
  isKept: boolean
  hasFile: boolean
}

export type VideoDetail = {
  id: string
  originalFileName: string
  status: VideoStatus
  durationSeconds: number | null
  createdAt: string
  updatedAt: string
  errorMessage: string | null
  transcript: Transcript | null
  clips: Clip[]
}

export type HealthStatus = {
  status: string
  name: string
  whisperConfigured: boolean
  highlightsConfigured: boolean
  highlightProvider: string
  transcriptionProvider: string
}

export type UploadResponse = {
  id: string
  status: VideoStatus
  originalFileName: string
}

