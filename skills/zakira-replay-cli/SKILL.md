---
name: zakira-replay-cli
description: Use the Zakira.Replay command-line tool to extract durable, timestamped, fact-shaped evidence from video URLs or local media. Zakira.Replay produces facts only; you synthesize summaries, work items, and other insights from those artifacts.
---

# Zakira.Replay CLI Skill

Use this skill when you can run shell commands and the user asks you to analyze, summarize, inspect, search, quote, clip, or extract work items from a video.

Zakira.Replay is an evidence producer. It writes transcripts, frames, OCR, vision notes, chapters, search indexes, manifests, and queue artifacts to disk. **It does not synthesize summaries, work items, decisions, or any other inferences.** Your job is to run the CLI, read the artifacts, and produce the user's requested answer from that evidence.

## Core Rule

Never claim you watched a video directly. Base every answer on `manifest.json`, `evidence.json`, `transcript.md`, frame images, `ocr/combined.md`, `vision/combined.md`, or `chapters/chapters.md`.

## When To Use

Use this skill for:

- YouTube, Vimeo, webinar, course, lecture, demo, meeting, or local media analysis.
- Requests that require transcript evidence, timestamps, visual inspection, OCR, clips, chapters, search, summaries (synthesized from evidence), or work-item extraction.
- Batch or queue processing where a human or agent can run local commands.
- Cases where durable disk artifacts are useful for later review or another agent.

Do not use this skill for:

- Text-only pages without video.
- Unauthorized downloads or bypassing access controls.
- Making claims before artifacts have been generated and inspected.

## Preflight

Run these before the first analysis in an environment or when dependency failures occur:

```powershell
zakira-replay doctor
zakira-replay deps path
```

If dependencies are missing and the user permits local downloads:

```powershell
zakira-replay deps install media
zakira-replay deps install onnx
zakira-replay deps install ocr [--language latin|chinese|english|korean|cyrillic|arabic|devanagari|greek|telugu|tamil]
zakira-replay deps install whisper-model    # default small; use --whisper-model <size> to pick
zakira-replay deps install diarization      # pyannote-segmentation + 3D-Speaker ONNX (~32 MB)
```

`media` installs portable `yt-dlp`, `ffmpeg`, and `ffprobe` where supported. `onnx` installs the default semantic-search model files. `ocr` installs the RapidOCR PP-OCRv5 latin models that the local (non-LLM) OCR provider needs (~30 MB across four files). Automatic downloads only happen when configured with `dependencies.autoDownload=true`, `search.onnx.autoDownload=true`, or `ocr.local.autoDownload=true`.

Dependency path overrides, if needed:

- `ZAKIRA_REPLAY_YTDLP_PATH`
- `ZAKIRA_REPLAY_FFMPEG_PATH`
- `ZAKIRA_REPLAY_FFPROBE_PATH`
- `ZAKIRA_REPLAY_ONNX_MODEL_PATH`
- `ZAKIRA_REPLAY_ONNX_VOCAB_PATH`
- `ZAKIRA_REPLAY_OCR_MODEL_DIRECTORY` (plus per-file `*_DETECTION_MODEL_PATH`, `*_CLASSIFICATION_MODEL_PATH`, `*_RECOGNITION_MODEL_PATH`, `*_DICTIONARY_PATH`)
- Config keys: `yt-dlp.path`, `ffmpeg.path`, `ffprobe.path`, `search.onnx.*`, `ocr.local.*`

Do not put secret values in JSON config. Config stores environment variable names for provider secrets.

## Recommended Analysis Commands

General evidence extraction (relies on the new defaults: `--frame-strategy scene`, `--ocr-provider local`, `--max-ai-frames 50`, `--scene-safety-cap 5000`, deterministic run-id):

```powershell
zakira-replay analyze "<url-or-file>" --ocr --vision --cache
```

Pin a run-id explicitly when you need a stable folder name beyond the auto-generated `<source-slug>-<sha8>`:

```powershell
zakira-replay analyze "<url-or-file>" --run-id <run-id> --ocr --vision --cache
```

Transcript-first analysis (no frames extracted):

```powershell
zakira-replay analyze "<url-or-file>" --frames 0 --frame-strategy interval --cache
```

Force LLM-backed OCR/vision (when the local OCR provider's accuracy isn't sufficient — typically for slides with tables, complex code blocks, or non-Latin scripts):

```powershell
zakira-replay analyze "<url-or-file>" --ocr --ocr-provider copilot --vision --cache
```

Audio fallback when no captions exist (still opt-in even with the new defaults):

```powershell
zakira-replay analyze "<url-or-file>" --ocr --vision --stt --cache
```

Authenticated videos:

```powershell
zakira-replay analyze "<url>" --browser-auth edge --frames 7 --frame-strategy scene --ocr --vision --cache
zakira-replay analyze "<url>" --cookies "<cookies.txt>" --frames 7 --cache
```

Always quote URLs in PowerShell, especially YouTube URLs containing `&`.

## Option Selection

Defaults that ship out-of-the-box: `--frame-strategy scene` (no `--frames` cap, bounded by `frames.sceneSafetyCap=5000`), `--ocr-provider local` (offline RapidOCR; first OCR run auto-downloads ~30 MB models from ModelScope unless `ocr.local.autoDownload=false`), `--max-ai-frames 50` (per-slide OCR/vision cap), `--frames 500` (only used when `--frame-strategy interval`), `frames.perMinute=12` (duration-aware floor for interval strategy). The auto-generated run-id is deterministic per source URL: `<slug>-<sha8>` so re-running the same source reuses the same run folder and `--cache` short-circuits cleanly.

Use these defaults unless the user says otherwise:

- `--cache`: include by default for LLM-backed work; use `--force` only when intentionally recomputing.
- `--frames 500` is the new general-analysis default for the `interval` strategy. Override down for cheap exploration (`--frames 30`) or up for very dense sampling (`--frames 5000` paired with `--frame-strategy interval`). When `--frame-strategy scene` is in effect (the default), `--frames` is ignored.
- `--frames 0 --frame-strategy interval`: transcript-only (no frames extracted).
- `--frame-strategy scene` (default): presentations, demos, UI walkthroughs, slide videos, conference talks, anything with discrete visual changes. Returns one frame per detected scene change, slide-grouping deduplicates. Total frame count scales with content, capped at `frames.sceneSafetyCap` (default 5000).
- `--frame-strategy interval`: dense uniform sampling, useful when you need a predictable count or when scene-detection produces too few frames (rare with the new default cap).
- `--frame-strategy every-frame`: only when the user explicitly needs capped frame-by-frame inspection.
- `--ocr`: enable when slides, code, dashboards, diagrams, documents, or burned-in captions may be visible.
- `--ocr-provider <name>`: choose the OCR backend. `local` (default) runs RapidOCR (PP-OCRv5) entirely on-device via ONNX — no LLM, no network at run-time after the one-time model download. Defaults to the **latin** language pack; switch packs for non-Latin scripts via `zakira-replay deps install ocr --language <pack>` + `zakira-replay config set ocr.local.languagePack <pack>` (or `ZAKIRA_REPLAY_OCR_LANGUAGE_PACK`). Supported packs: `latin`, `chinese`, `english`, `korean`, `cyrillic`, `arabic`, `devanagari`, `greek`, `telugu`, `tamil`. `copilot` routes the image through the configured LLM (GitHub Copilot, OpenAI, Azure OpenAI, or Ollama) using vision-capable chat models — prefer this for complex layouts, mixed scripts, or when `tables[]` reconstruction matters (the local provider leaves `tables[]` empty in this release). The first local-OCR run auto-downloads ~30 MB of models (set `ocr.local.autoDownload=false` to disable; pre-install with `zakira-replay deps install ocr [--language <pack>]`).
- `--vision-provider <name>` + `--local-vision-mode <mode>`: choose the vision backend. `copilot` (default) routes per-slide vision through the configured LLM. `local` runs the fully-on-device `LocalOnnxVisionProvider` that never invokes an LLM. Under `local`, pick one of three sub-modes via `--local-vision-mode`: `heuristic` (zero models, structure derived from OCR; works out of the box), `clip` (heuristic + CLIP ViT-B/32 zero-shot for the `kind` field, ~150 MB user-supplied ONNX), or `clip-blip` (default for the local provider; CLIP + BLIP image captioning fills `freeText`, ~550 MB total). When `--vision-provider local` is passed without `--ocr`, OCR is auto-enabled and `VISION_LOCAL_OCR_REQUIRED` (info) is emitted — the structured fields need OCR. Missing CLIP/BLIP files cause graceful degradation (`clip-blip` → `clip` → `heuristic`) with a `VISION_LOCAL_MODE_DEGRADED` warning. Limitations: `charts[]` is always empty in local mode, BLIP captions are noisier than a frontier vision LLM. The chosen provider is recorded on each `VisionFrameResult.provider`. Configure model paths with `zakira-replay config set vision.local.clipImageEncoderPath /path/to/clip-image-encoder.onnx` etc.
- `--vision`: enable when visual content matters.
- `--smart-crop` / `--smart-crop-profile <profile>`: enable smart-crop preprocessing that removes meeting-platform UI chrome (Teams/Zoom/WebEx controls bar, participant gallery sidebar, black letterbox bars, bottom navigation) before perceptual hashing, OCR, and vision. Profiles: `auto` (default), `teams`, `zoom`, `webex`, `generic` (all share the same algorithm in this release), or `off` to disable. Use this when the source is a meeting recording — it dramatically improves slide-grouping stability (the persistent gallery sidebar otherwise dilutes the dHash) and removes meeting-app vocabulary from OCR text. Set `crop.enabled=true` in config to make it the default for all runs.
- `--capture-mode {auto|ytdlp|browser}`: choose the frame-capture backend. `ytdlp` (default) uses yt-dlp + ffmpeg — works for ~1000 sites yt-dlp supports plus local files. `browser` drives Playwright-controlled Chromium (pinned to Edge) to navigate, click play, JS-seek, and screenshot — required for SharePoint/Medius/Teams recordings and any source yt-dlp can't reach. `auto` tries yt-dlp first and falls back to `browser` on failure, emitting `CAPTURE_BROWSER_FALLBACK` so orchestrators can branch on which path was used. For authenticated sources, combine with `--cookies-from-browser edge` (yt-dlp-side) or rely on Edge's existing session in browser mode. **Side benefit:** when browser capture runs, a network listener watches for any `.vtt`/`.srt` responses the page fetches, persists them under `captions/browser-NNNN.vtt`, indexes them in `captions/discovered.json`, and — if no transcript was found by yt-dlp/sidecar/STT — picks the best-language match (using `--caption-languages` and the source's primary language as hints) and uses it to populate `transcript.md`. This is the easiest way to get transcripts for Medius/Ignite/MVP-Summit sessions and any custom player whose page-side JS fetches a caption file.
- `--auth-profile <name>`: load a previously-saved Playwright storage-state profile into the browser context. Required for SSO-gated sources (SharePoint Stream, Microsoft Stream, internal corporate portals, Microsoft event playbacks behind Microsoft accounts). Only consulted in `browser` and `auto` capture modes. Create the profile interactively with `zakira-replay auth login <name>`. The pipeline emits `AUTH_PROFILE_NOT_FOUND` (error) when the named profile does not exist on disk and `AUTH_PROFILE_STALE` (info) when the profile's file mtime is older than `auth.staleThresholdMinutes` (default 60). Staleness is informational — capture proceeds; orchestrators should suggest re-running `auth login` when downstream extraction looks like it landed on a login page instead of the intended content.
- `--stt`: enable when captions may be absent or poor. Captions/sidecars are tried first; STT only runs if transcript extraction fails.
- `--diarize` / `--num-speakers <n>` / `--diarize-threshold <0.0-1.0>`: run local sherpa-onnx speaker diarization (pyannote-segmentation-3.0 + 3D-Speaker embedding + agglomerative clustering) on top of the transcript. Requires a transcript (`--stt` or captions). Diarization rewrites `transcript.md` in place with `[SPEAKER_NN]` prefixes; the per-speaker registry in `evidence.json` and the per-slide / per-chapter speaker rollups in `evidence-aligned/` are then populated automatically. Pass `--num-speakers` when you know how many speakers are present; otherwise `--diarize-threshold` (default 0.5) controls the cluster cutoff (lower → more speakers). Pre-install models with `zakira-replay deps install diarization` (~32 MB). Speaker IDs are anonymous within a run (`SPEAKER_00`, `SPEAKER_01`, …) and have no cross-run meaning. Explicit caption-side attribution (VTT `<v>` tags / SRT prefixes) is preserved — diarization never overwrites a known speaker name. Warning codes: `DIARIZATION_NO_AUDIO`, `DIARIZATION_NO_TRANSCRIPT`, `DIARIZATION_MODELS_MISSING`, `DIARIZATION_INIT_FAILED`, `DIARIZATION_FAILED`, `DIARIZATION_UNKNOWN_PROVIDER`.
- `--caption-languages`: comma-separated language preferences for yt-dlp subtitles (e.g. `--caption-languages fr,en`). Defaults to `auto`, which unions the source's primary language, the languages with **manually uploaded** subtitles (per `info.subtitles`), English (`en`, `en.*`), and YouTube live-chat. YouTube auto-translation languages (those that appear only under `info.automatic_captions`) are intentionally **not** expanded by `auto` because they are inferences from the source, not facts about what was spoken. To opt into a specific auto-translation, pass that language explicitly (`--caption-languages es`); read `metadata.json -> availableSubtitleLanguages` first to see which languages exist (`hasManual` / `hasAuto`) for the source. Stable IDs for any frames that get extracted are written to both `frames[*].id` and `ocr[*].frameId` / `vision[*].frameId` for cross-reference.
- `--vision-instruction`: optional focus signal appended to the vision prompt. The default is empty; the model already extracts every visible piece of content (slide titles, bullets, code blocks, chart axes, UI controls). Use this only to bias enumeration order toward what matters for the orchestrator's question (e.g. `"Bias toward chart axes and code"`).
- `--ocr-instruction`: optional focus signal appended to the OCR prompt. The default is empty; the model already extracts every readable character. Use this for hints like `"Preserve indentation in code-like text"`. Both instructions are persisted into `evidence.json::visionInstruction` and `evidence.json::ocrInstruction` for audit. They never relax the "do not invent" guardrails. The local OCR provider ignores `--ocr-instruction` entirely (it always extracts every visible character) but still persists the value for audit.
- `--frames-per-minute <n>`: per-request override of the duration-aware sampling rate for the interval strategy. The config default is `frames.perMinute=12` (one frame every 5 seconds). When set (or non-zero in config), the effective frame count is `max(framesPerMinute * durationMinutes, --frames)`. Pass `--frames-per-minute 0` to disable duration-aware scaling for one run. Ignored for `scene` and `every-frame`.
- `--max-ai-frames <n>`: cap on the number of unique slides sent to OCR/vision. Default `50`. Slide grouping deduplicates extracted frames first; this then bounds the AI cost. Lower for cheap runs, higher when slide-deck content is dense.
- `--scene-safety-cap <n>`: per-run override of `frames.sceneSafetyCap` (default 5000). The scene strategy returns up to this many frames; slide grouping deduplicates. When the cap is hit the run carries a `FRAMES_SCENE_CAP_REACHED` warning. The pipeline also emits `FRAMES_LIKELY_UNDERSAMPLED` if interval sampling without `--frames-per-minute` (and with `frames.perMinute=0` in config) produces fewer than 1 frame per 5 minutes.

There is no `--summary` flag. Synthesis is your job, not Zakira.Replay's.

Provider notes:

- `github-copilot` is the default LLM provider for STT (and for OCR/vision when `--ocr-provider copilot`).
- `openai` supports chat/image and audio transcription via `/audio/transcriptions`.
- `azure-openai` supports chat/image for OCR/vision, but Zakira.Replay STT is not implemented yet.
- `ollama` talks to a local Ollama daemon (`http://localhost:11434` by default) through OllamaSharp's native `Microsoft.Extensions.AI.IChatClient` implementation. **Chat / vision only** — no STT. Configure with `llm.ollama.model` (chat), `llm.ollama.visionModel` (image attachments), and `llm.ollama.endpoint` (or env vars `ZAKIRA_REPLAY_OLLAMA_*`, `OLLAMA_HOST`). Pre-pull models with `ollama pull qwen2.5:7b` / `ollama pull llama3.2-vision:11b`. Combine with `--llm-provider local-whisper` for STT and `--ocr-provider local` for OCR to get an air-gapped run.
- `local-whisper` runs Whisper.net (whisper.cpp bindings) entirely on-device for STT. **STT-only** — has no chat/vision/OCR surface; combine with `--ocr-provider local` for a fully-offline run. Pre-install the model with `zakira-replay deps install whisper-model [--whisper-model tiny|base|small|medium|large-v3|large-v3-turbo]` (default: `small`, ~466 MB). Configure via `llm.localWhisper.*` keys or `ZAKIRA_REPLAY_WHISPER_*` env vars. Surface-specific warnings: `STT_LOCAL_MODEL_MISSING`, `STT_LOCAL_INIT_FAILED`, `STT_LOCAL_INFERENCE_FAILED`.
- The default OCR provider is `local` (RapidOCR via ONNX) which needs no LLM at all.

## Read Command Output

After `analyze`, capture:

- `Completed run:` or `Reused run:`
- `Artifacts:` directory
- `Manifest:` path
- Any `Warnings:` lines (formatted as `[severity] CODE: message`)

If the command reports `Reused run`, inspect existing artifacts before deciding whether `--force` is needed.

## Artifact Reading Order

Read artifacts in this order:

1. `manifest.json`: produced paths, structured warnings, run ID, frame list.
2. `evidence.json`: structured transcript, frames, slides, OCR, vision, per-speaker registry (`speakers[]`), structured warnings.
3. `slides/slides.json` (also embedded in `evidence.json`): slide grouping facts (`firstSeenSeconds`, `lastSeenSeconds`, `frameIds`, `primaryFrameId`). OCR/vision run once per slide; each result carries `slideId`.
4. `transcript.md`: human-readable timestamped transcript with `[Speaker Name]` prefixes when captions carried speaker tags.
5. `transcript/normalization.json` and `transcript/raw.*`: audit exact quotes when normalization matters. Speaker changes are hard merge boundaries.
6. `audio/chunks/chunks.json`: present only when long-audio STT was silence-chunked. Branch on `STT_CHUNK_FAILED` warnings if any chunk failed.
7. `ocr/{frameId}.json` and `ocr/combined.md`: structured OCR (`freeText`, `lines[]`, `tables[]`); branch on `OCR_PARSE_FALLBACK` for prose responses.
8. `vision/{frameId}.json` and `vision/combined.md`: structured vision (`kind`, `title`, `bullets[]`, `codeBlocks[]`, `charts[]`, `uiElements[]`, `freeText`); branch on `VISION_PARSE_FALLBACK`.
9. `frames/`: inspect images when layout, UI, charts, code, slides, or visual details matter.
10. `metadata.json`: title, source URL, duration, uploader metadata, `availableSubtitleLanguages`.
11. `evidence.md`: concise human-readable index of artifact paths.

Speakers in `evidence.speakers[]` carry `id` (slug, stable), optional `displayName`, plus `segmentCount`, `totalSeconds`, `firstSeenSeconds`, `lastSeenSeconds`. Each `transcript[*]` segment has `id` (`segment-NNNN`) and may have `speakerId`/`speakerDisplayName`. STT-derived transcripts do not carry speakers in this release.

Warnings in `manifest.json` and `evidence.json` are structured records: `{ code, message, source, severity }`. Branch on `code` (for example `TRANSCRIPT_NOT_FOUND`, `STT_NO_LLM_PROVIDER`, `STT_CHUNK_FAILED`, `OCR_PARSE_FALLBACK`, `OCR_LOCAL_MODELS_MISSING`, `OCR_LOCAL_INFERENCE_FAILED`, `OCR_UNKNOWN_PROVIDER`, `VISION_PARSE_FALLBACK`, `PERCEPTUAL_HASH_FAILED`, `FRAMES_REMOTE_FALLBACK`, `CROP_BAIL_OUT`, `CROP_PROFILE_UNKNOWN`, `CROP_IMAGE_DECODE_FAILED`, `CROP_OUTPUT_FAILED`, `CAPTURE_BROWSER_FALLBACK`, `CAPTURE_BROWSER_UNAVAILABLE`, `CAPTURE_PLAY_BUTTON_NOT_FOUND`, `CAPTURE_DURATION_UNRESOLVED`, `CAPTURE_SEEK_FAILED`, `CAPTURE_SCREENSHOT_FAILED`, `CAPTURE_UNKNOWN_MODE`, `CAPTIONS_BROWSER_NETWORK_NONE`, `CAPTIONS_BROWSER_NETWORK_DOWNLOAD_FAILED`, `CAPTIONS_BROWSER_NETWORK_PARSE_FAILED`, `AUTH_PROFILE_NOT_FOUND`, `AUTH_PROFILE_STALE`, `AUTH_PROFILE_LOAD_FAILED`) rather than fuzzy-matching the message.

## Chapters And Search

Build chapters after transcript evidence exists:

```powershell
zakira-replay chapters build runs\<run-id> --min-duration 60 --max-duration 600
```

Chapters are pure time spans plus per-chapter evidence references. Generate any titles or prose summaries you need yourself; the tool does not produce them.

Materialise cross-modal alignment views after chapters and slides exist:

```powershell
zakira-replay align runs\<run-id>
```

This writes `evidence-aligned/by-chapter.json` (per-chapter join of slides, transcript segment IDs, OCR/vision frame IDs, and speaker stats) and `evidence-aligned/by-slide.json` (per-slide join of frames, OCR, vision, transcript segment IDs spoken while the slide was visible, speaker stats, and overlapping chapter indices). Both files share `evidence-aligned.schema.json` and are pure rearrangements with no model calls.

Build a search index for repeated questions or long transcripts:

```powershell
zakira-replay search build runs\<run-id> --backend sqlite-onnx
zakira-replay search query runs\<run-id> "<question or topic>" --top 10 --backend auto
```

Backend choice:

- `json`: portable sparse TF-IDF fallback.
- `sqlite`: SQLite FTS5 keyword search.
- `sqlite-onnx`: semantic search, best for natural-language retrieval, requires ONNX model and vocabulary.

Treat search matches as pointers into evidence, not final answers by themselves.

## Clips

Extract clips only when timestamps are known or justified by artifacts:

```powershell
zakira-replay clip "<url-or-file>" --start 01:20 --end 02:05 --output-name key-demo
```

Read `clip.json` and report the clip path plus timestamp range.

## Ad-hoc Frame Capture

`zakira-replay frames` has two modes:

1. **Legacy mode** (no `--at`/`--from`/`--to`): runs a frames-only full-analyze pipeline. Equivalent to `analyze --no-transcript`. Keep using this when you actually want slides/OCR/vision.
2. **Ad-hoc mode** (any of `--at`, `--from`, `--to` present): cheap spot capture via `FrameCaptureService` - no slide grouping, no OCR, no vision, no chapter synthesis. Use this after a full `analyze` run when an agent needs additional stills for a downstream artifact (e.g. recipe step images, transcript-aligned thumbnails, screenshots at known timestamps for a bug report).

Output for ad-hoc mode lands in a new `runs/<id>/frames/` folder alongside a minimal `frame-capture.json` manifest (schema: `frame-capture.schema.json`, `kind: "frame-capture"`).

```powershell
# Exact timestamps (comma-separated; accepts seconds, MM:SS, HH:MM:SS)
zakira-replay frames "./cooking.mp4" --at 02:34,03:10,04:55 --max-edge 1024 --quality 85

# Window with N evenly spaced frames (endpoints inclusive)
zakira-replay frames "https://example.com/video" --from 02:00 --to 03:00 --count 5

# Window with ffmpeg scene-cut detection scoped to the window
zakira-replay frames "./demo.mp4" --from 02:00 --to 03:00 --strategy scene --scene-safety-cap 20

# JSON output (same shape as the extract_frames MCP tool result)
zakira-replay frames "./demo.mp4" --at 02:34 --json
```

Ad-hoc flag cheatsheet:

- `--at <ts1,ts2,...>`: list of exact timestamps. Up to 64 per call; excess are dropped with `FRAME_CAPTURE_TOO_MANY_TIMESTAMPS`. Out-of-range entries are dropped with `FRAME_CAPTURE_TIMESTAMP_OUT_OF_RANGE`.
- `--from <ts>` / `--to <ts>`: time window. Required together. `--to` is clamped to source duration with `FRAME_CAPTURE_RANGE_OUT_OF_BOUNDS`.
- `--count <n>`: number of frames inside the window. For `--strategy interval`, evenly spaced inclusive of both endpoints. For `--strategy scene`, acts as an upper bound on returned scene cuts.
- `--strategy interval|scene`: defaults to `interval`. `scene` runs ffmpeg's scene-cut filter scoped to the window via output-side `-ss`/`-to`; reported timestamps stay in absolute source timeline.
- `--max-edge <px>`: resize so the longest edge is at most N pixels (aspect ratio preserved). Useful for thumbnail-sized stills.
- `--quality <1-100>`: JPEG quality (mapped to ffmpeg qscale 31-2). Default high quality.
- `--phash`: also compute a 64-bit perceptual hash per frame so the agent can dedupe near-identical stills downstream.
- `--scene-safety-cap <n>`: hard cap on scene cuts in the window (defaults to `max(--count, 200)`). Emits `FRAME_CAPTURE_SCENE_CAP_REACHED` when reached.
- `--json`: emit machine-readable output (runId, artifactDirectory, manifestPath, frameCount, frames[], warnings) instead of the human-readable per-frame summary.
- `--cookies` / `--cookies-from-browser` / `--browser-auth`: yt-dlp auth for remote sources, identical semantics to `analyze`.
- `--run-id <id>`: pin the artifact folder name; otherwise auto-generated from the source.

`--at` and `--from`/`--to` are mutually exclusive; passing both raises a CLI error before ffmpeg runs.

Frame-capture-specific warning codes (also written into `manifest.warnings`):

- `FRAME_CAPTURE_TIMESTAMP_OUT_OF_RANGE` - timestamp was negative or past source duration.
- `FRAME_CAPTURE_RANGE_OUT_OF_BOUNDS` - `--to` exceeded source duration and was clamped.
- `FRAME_CAPTURE_TOO_MANY_TIMESTAMPS` - >64 timestamps supplied; only the first 64 were used.
- `FRAME_CAPTURE_NO_FRAMES` - ffmpeg returned zero frames (e.g. scene detection found nothing in the window).
- `FRAME_CAPTURE_SCENE_CAP_REACHED` - safety cap was hit during scene detection.
- `FRAME_CAPTURE_MEDIA_URL_UNRESOLVED` - yt-dlp could not resolve a direct media URL; the pipeline fell back to downloading.

Do not reach for `frames --at`/`--from`/`--to` when you actually need transcript, slides, OCR, vision, chapters, or evidence alignment; use `analyze` for those.

## Queue And Batch

Use queue commands when many videos need local processing:

```powershell
zakira-replay queue enqueue "<url-or-file>" --queue-id research --job-id <job-id> --frames 7 --cache
zakira-replay queue run --queue-id research --concurrency 2 --retries 2
zakira-replay queue status --queue-id research --json
```

Use batch manifests when the user already has a manifest file:

```powershell
zakira-replay batch run <manifest.json>
```

## Topic Summary And Work Items Pattern

For requests like "watch this and summarize topics and work items":

1. Run slide/demo-heavy analysis with `--stt --ocr --vision --frames 30 --frame-strategy scene --cache` unless the user requests cheaper settings.
2. Build chapters with `zakira-replay chapters build`.
3. Build semantic search with `zakira-replay search build --backend sqlite-onnx` when available.
4. Read `chapters/chapters.md`, `evidence.json`, `transcript.md`, and `ocr/combined.md`.
5. Search for `action item`, `next steps`, `todo`, `follow up`, `decision`, `owner`, `deadline`, and relevant project terms.
6. Synthesize the topic summary and work items yourself from these facts. Write the final Markdown alongside the run, usually `runs/<run-id>/work-items.md`, if the user asked for a durable output file.

Work item format:

```markdown
- [ ] OWNER -- TASK -- DUE (or "unspecified") -- [HH:MM:SS] -- evidence: "short verbatim quote"
```

Do not invent owners or due dates. Use `unspecified` when unclear. Deduplicate repeated commitments and keep the earliest strong timestamp.

## Failure Handling

If dependency-related:

- Run `zakira-replay doctor` and `zakira-replay deps path`.
- Suggest `zakira-replay deps install media` for missing `yt-dlp`/`ffmpeg`/`ffprobe`.
- Suggest `zakira-replay deps install onnx` for semantic search model files.

If access-related:

- Use `--cookies <file>`, `--cookies-from-browser <browser>`, or `--browser-auth <browser>` only when the user has legitimate access.
- For sites yt-dlp cannot reach at all (authenticated SharePoint portals, Medius/Teams playback URLs, custom corporate players), use `--capture-mode browser` so frames are captured by Playwright directly. Combine with `--cookies-from-browser edge` if the page also needs session cookies for the initial load.
- For SSO-gated sources (Microsoft 365 / Azure AD / Okta), create a persistent auth profile interactively with `zakira-replay auth login <profile-name>`, then pass `--auth-profile <profile-name>` on every subsequent `analyze` invocation. List existing profiles with `zakira-replay auth list`. Profiles older than `auth.staleThresholdMinutes` (default 60) emit `AUTH_PROFILE_STALE`; refresh by re-running `auth login` with the same name.

If transcript is missing:

- Rerun with `--stt`.
- Remember Azure OpenAI STT is not implemented; use GitHub Copilot or OpenAI for STT-required runs.

If visual evidence is sparse:

- Rerun with more `--frames`, `--frame-strategy scene`, `--ocr`, or `--vision`.
- Use `--frame-strategy every-frame` only with a tight `--frames` cap.

If AI provider calls fail:

- Preserve warnings in the final answer. Branch on warning `code`.
- Rerun with `--force` only if recomputation is worth the cost.
- For repeated OCR/vision failures, reduce `--frames` or switch provider/model if configured.
- If the LLM-backed OCR is unreliable or unavailable, fall back to `--ocr-provider local` (after `zakira-replay deps install ocr`). The local provider doesn't need any LLM and is unaffected by Copilot/OpenAI/Azure outages. Tradeoff: lower OCR fidelity on complex layouts and no `tables[]` reconstruction.

## Evidence Discipline

When answering:

- Lead with the answer, then cite timestamped evidence.
- Separate confirmed evidence from inference.
- Mention warnings (by `code`) that affect confidence.
- Keep transcript excerpts short unless the user asks for extensive quotes.
- Do not fabricate speakers, slide contents, UI text, numbers, decisions, or work items.
- If evidence is insufficient, say so and recommend a concrete rerun command.
