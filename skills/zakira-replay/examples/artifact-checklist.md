# Artifact Checklist

Use this checklist after a Zakira.Replay job succeeds.

Zakira.Replay produces facts only. Summaries, work items, decisions, and other insights are the orchestrating agent's responsibility.

## Required First Reads

- `manifest.json`: confirm paths, produced artifacts, structured warnings, and run ID.
- `evidence.json`: load structured evidence and structured warnings.

## Transcript Evidence

- Read `transcript.md` when present.
- Use `transcript/raw.md`, `transcript/raw.json`, and `transcript/normalization.json` when you need to audit whether caption normalization merged or removed repeated fragments.
- Prefer timestamped transcript segments for claims and quotes.
- When captions carried speaker tags, segments include `speakerId` (slug) and `speakerDisplayName`. A per-speaker registry under `evidence.speakers[]` summarises segment counts and total speaking time. STT-derived transcripts do not carry speakers in this release.
- The transcript can come from four sources: yt-dlp captions, a local sidecar `.vtt`/`.srt`, STT (when `stt: true` was set), or the browser-network interceptor (when `captureMode: "browser"` or `"auto"` was used and the page fetched a caption file). Check the underlying `TranscriptArtifact.kind` to know which.
- If transcript is absent and audio matters, rerun with `stt: true`. Long audio is silence-chunked automatically; chunk metadata lands under `audio/chunks/chunks.json` when chunking actually fired, and per-chunk failures appear as `STT_CHUNK_FAILED` warnings.
- If transcript is absent and the source is a JS-rendered player (custom enterprise portal, Microsoft Medius, etc.), rerun with `captureMode: "browser"`. The Playwright network listener captures any `.vtt`/`.srt` the page fetches, persists them to `captions/browser-NNNN.vtt`, indexes them in `captions/discovered.json`, and uses the best-language match (per `captionLanguages` and the source's primary language) to populate `transcript.md`.

## Browser-Discovered Captions (`captures/discovered.json`)

Present only when browser capture ran and observed at least one caption response on the wire. Schema: `captions-discovered.schema.json`. Each entry has:

- `url` — original network URL with all query params intact (SAS tokens preserved for audit).
- `relativePath` — persisted file path under the run (e.g. `captions/browser-0001.vtt`).
- `inferredLanguage` — best-effort BCP-47 code from one of: `url-Caption_<lang>` (Microsoft Medius style), `url-filename` (`subtitle_es-ES.vtt`), `url-path-segment` (`/captions/en/`), `url-query-{lang|hl|language|l|tlang}`. May be `null` when no signal is present.
- `languageSource` — identifier of the heuristic that produced `inferredLanguage`; useful for triaging false positives.
- `byteCount`, `contentType`, `contentSha256` — per-file accounting (the SHA dedupes across runs).
- The top-level `originalLanguage` field is the source's primary language as reported by yt-dlp metadata, useful for picking the "main" track when several languages are captured.

## Visual Evidence

- Read `ocr/combined.md` for visible text from frames.
- Read `vision/combined.md` for visual descriptions.
- Inspect frame files when the user asks about layout, diagrams, UI, code, charts, or visual details.
- Each `OcrFrameResult.provider` records whether the result came from `"copilot"` (LLM vision-as-OCR) or `"local"` (RapidOCR via ONNX). Local-OCR results are typically lower fidelity on complex layouts and leave `tables[]` empty (no layout-analysis-based table reconstruction in this release); prefer `"copilot"` when `tables[]` matters.
- Each `FrameArtifact` may carry optional `width`, `height`, `crop` (the rectangle), and `originalPath` (the pre-crop frame) when smart-crop ran. The frame's `path` then points to the cropped variant; the perceptual hash and downstream OCR/vision were computed on the crop, not the original.
- If frames are sparse, rerun with more `frames` or `frameStrategy: "scene"`.
- For meeting recordings (Teams/Zoom/WebEx), enable `smartCrop: true` (CLI: `--smart-crop`) so the persistent UI chrome is removed before slide grouping. This dramatically improves slide stability and removes meeting-app vocabulary ("Take control", "Raise", "Mute all", etc.) from OCR text.

## Search Evidence

- Build `search/index.json` for repeated Q&A over a run.
- Query the index before reading the full transcript when the user asks about a specific topic.
- Treat search matches as pointers into evidence, not final answers by themselves.

## Clips

- Use clip extraction only when start/end timestamps are known or can be justified from artifacts.
- Save clip paths from `clip.json` and report them with the timestamp range.

## Auth Profiles (SSO-Gated Sources)

- Created interactively via the CLI (`zakira-replay auth login <profile-name>`); cannot be created from MCP.
- Stored under `<config-dir>/auth/<slug>.json`. List with `auth list`, inspect with `auth show <name>`, delete with `auth clear <name>`.
- Pass `authProfile: "<name>"` along with `captureMode: "browser"` (or `"auto"`) to load the saved cookies into the headless capture session.
- The pipeline emits `AUTH_PROFILE_NOT_FOUND` (error) when the named profile is absent and `AUTH_PROFILE_STALE` (info) when the profile's mtime is older than `auth.staleThresholdMinutes` (default 60). Staleness does not block the run; if downstream extraction looks like it landed on a login page, suggest the user re-run `auth login <name>` with the same name to refresh cookies.

## Warnings

- Warnings are structured records: `{ code, message, source, severity }`.
- Branch on `code`, not on the message text.
- Known codes (one line per code, severity in parentheses):
  - `TRANSCRIPT_NOT_FOUND` (warning) / `TRANSCRIPT_NOT_FOUND_NO_STT` (warning) — captions missing; the `_NO_STT` variant fires when `stt` was not set so STT fallback was skipped.
  - `MEDIA_URL_UNRESOLVED` (error) — yt-dlp could not extract a direct media URL.
  - `AUDIO_REMOTE_FALLBACK` (info) / `AUDIO_DOWNLOAD_FAILED` (error) — direct audio extract failed; pipeline tried local-download fallback.
  - `STT_NO_AUDIO` / `STT_NO_LLM_PROVIDER` (error) / `STT_CHUNK_FAILED` (warning) — speech-to-text failures.
  - `FRAMES_NO_MEDIA` / `FRAMES_REMOTE_FALLBACK` (info) / `FRAMES_DOWNLOAD_FAILED` (error) / `FRAMES_SCENE_CAP_REACHED` (warning) / `FRAMES_LIKELY_UNDERSAMPLED` (warning) — frame-extraction issues.
  - `OCR_NO_LLM_PROVIDER` (error) / `OCR_PARSE_FALLBACK` (warning) / `OCR_LOCAL_MODELS_MISSING` (error) / `OCR_LOCAL_INIT_FAILED` (error) / `OCR_LOCAL_INFERENCE_FAILED` (warning) / `OCR_UNKNOWN_PROVIDER` (error) — OCR-side issues. The `OCR_LOCAL_*` codes only fire under `ocrProvider: "local"`.
  - `VISION_NO_LLM_PROVIDER` (error) / `VISION_PARSE_FALLBACK` (warning) — vision-side issues.
  - `PERCEPTUAL_HASH_FAILED` (warning) — slide grouping may be coarse for at least one frame.
  - `CROP_IMAGE_DECODE_FAILED` (warning) / `CROP_BAIL_OUT` (info) / `CROP_PROFILE_UNKNOWN` (warning) / `CROP_OUTPUT_FAILED` (warning) — smart-crop issues. `CROP_BAIL_OUT` is informational — the algorithm proposed a too-aggressive crop and used the original frame instead.
  - `CAPTURE_BROWSER_UNAVAILABLE` (error) / `CAPTURE_BROWSER_FALLBACK` (info) / `CAPTURE_PLAY_BUTTON_NOT_FOUND` (info-or-warning) / `CAPTURE_DURATION_UNRESOLVED` (error) / `CAPTURE_SEEK_FAILED` (warning) / `CAPTURE_SCREENSHOT_FAILED` (warning) / `CAPTURE_UNKNOWN_MODE` (warning) — Playwright capture issues.
  - `CAPTIONS_BROWSER_NETWORK_NONE` (info) / `CAPTIONS_BROWSER_NETWORK_DOWNLOAD_FAILED` (warning) / `CAPTIONS_BROWSER_NETWORK_PARSE_FAILED` (warning) — browser caption interceptor results.
  - `AUTH_PROFILE_NOT_FOUND` (error) / `AUTH_PROFILE_STALE` (info) / `AUTH_PROFILE_LOAD_FAILED` (error) — auth profile resolution issues.
  - `CLIP_MEDIA_URL_UNRESOLVED` (error) — clip extraction couldn't resolve the source.
- Treat missing captions, missing media URL, failed OCR, failed vision, fallback downloads, undersampled frame coverage, and stale auth profiles as confidence modifiers — none of them prevent the run from completing, but all of them affect what claims the orchestrator can make.

## Response Quality

- Cite timestamps where possible.
- Keep raw transcript excerpts short unless the user asks for extensive quotes.
- Do not invent speaker names, slides, charts, or numbers not present in artifacts.
- Mention by `code` any warnings that affect confidence in the answer.
- If evidence is insufficient, say so and recommend a concrete rerun option (e.g. "rerun with `stt: true`" / "rerun with `captureMode: \"browser\"`" / "rerun with `smartCrop: true` for this Teams recording" / "ask the user to re-run `auth login <name>` and try again").
