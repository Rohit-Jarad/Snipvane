# Snipvane

Long-form video in. Ranked vertical shorts with karaoke subtitles out.

Default path is **free**: Gemini transcribes (chunked for long videos) and picks highlight moments. FFmpeg cuts 9:16 clips with karaoke captions. Nothing is auto-published.

## Free quota (Gemini)

OpenAI Whisper is paid. Snipvane uses **Gemini Flash** instead.

Exact numbers change; check [Google AI Studio rate limits](https://aistudio.google.com/rate-limit) for your key. Typical **free Flash** ballpark:

- About **5–15 requests/minute**
- About **100–1,500 requests/day** (newer models are often tighter)

This app’s usage:

| Video length | Gemini calls (approx) |
| --- | --- |
| ~1 minute | 2 (1 transcript + 1 highlights) |
| ~8 minutes | 2 |
| 1 hour | ~9 (8 audio chunks + 1 highlights) |
| 2 hours | ~16 |

So on a **1,500/day** cap you could process hundreds of short clips, or on a **100/day** cap maybe ~50 one-minute videos or ~6 one-hour videos. If you hit 429, the app retries; if the daily cap is gone, wait until it resets.

## Local run

1. .NET 10 SDK, Node 20+, SQL Server Express, FFmpeg
2. Copy `Snipvane/appsettings.Local.json.example` to `Snipvane/appsettings.Local.json` (gitignored) and set `Ai:GeminiApiKey` only
3. `cd Snipvane && dotnet run --launch-profile http`
4. `cd client && npm install && npm run dev` → http://localhost:5173

Local SQL Server:

```
Server=localhost\SQLEXPRESS;Database=SnipvaneDb;Trusted_Connection=True;TrustServerCertificate=True;
```

## GitHub (public) — do not commit secrets

`appsettings.Local.json` is gitignored. Never put API keys in the public repo.

```powershell
git init
git add .
git commit -m "Add Snipvane video-to-shorts pipeline."
git remote add origin https://github.com/<you>/Snipvane.git
git push -u origin main
```

## Render

Render has **no SQL Server**. The app uses SQL Server locally and **Postgres** when `DATABASE_URL` is set.

1. Push this repo to GitHub
2. [Render](https://render.com) → New → Web Service → this repo → Docker
3. Create a Postgres database (Render paid, or free [Neon](https://neon.tech) / [Supabase](https://supabase.com)) and paste the URL into `DATABASE_URL`
4. Set env vars:

```
Ai__GeminiApiKey=<your gemini key>
Ai__TranscriptionProvider=Gemini
Ai__GeminiModel=gemini-3.6-flash
DATABASE_URL=postgres://...
```

5. Deploy. Health check: `/api/health`

**Free Render caveats:** the instance sleeps, local video files are **ephemeral** (lost on restart unless you add a persistent disk), and a 2 GB upload may time out. Fine for demos; use a disk or object storage for real use.

`render.yaml` is in the repo if you prefer Blueprint deploy. Put the Gemini key in the Render dashboard, not in git.
