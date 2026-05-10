---
name: zakira-replay-mcp
description: Use Zakira.Replay's MCP server tools to run non-blocking video analysis jobs, retrieve timestamped evidence artifacts, build chapters/search indexes, and extract clips. Zakira.Replay produces facts only; you synthesize summaries, work items, and other insights from those artifacts.
---

# Zakira.Replay MCP Skill

Use this skill when Zakira.Replay is available as an MCP server and the user asks you to analyze, summarize, inspect, search, quote, clip, or extract work items from a video.

Zakira.Replay is an evidence producer. It writes durable, fact-shaped artifacts to disk and returns artifact paths through MCP. **It does not synthesize summaries, work items, decisions, or any other inferences.** Your job is to call MCP tools, read the produced artifacts, and synthesize the user's requested answer from those files.

## Core Rule

Never claim you watched a video directly. Base every answer on artifacts returned by MCP: `manifest.json`, `evidence.json`, `transcript.md`, frame images, `ocr/combined.md`, `vision/combined.md`, or `chapters/chapters.md`.

## MCP Server

The server command is:

```bash
zakira-replay mcp serve
```

If `zakira-replay` is not on `PATH`, configure the MCP client to use the full executable path or a development `dotnet run` command.

Use `doctor` as the first tool when dependency or provider readiness is unknown.

## Tool Selection

Prefer these tools:

- `create_analysis_job`: start non-blocking analysis and get a `jobId`.
- `get_job_status`: poll logs and status.
- `get_job_result`: fetch completed manifest and artifact directory.
- `build_chapters`: build `chapters/chapters.json` and `chapters/chapters.md` for a completed run.
- `build_search_index`: build JSON, SQLite, or SQLite+ONNX search over a completed run.
- `query_search_index`: retrieve relevant evidence chunks.
- `extract_clip`: create a timestamped clip when start/end are known.
- `build_evidence_alignment`: build cross-modal alignment views (`by-chapter`, `by-slide`) over a completed run. Pure rearrangement; no model calls.
- `doctor`: diagnose dependencies and provider setup.
- `enqueue_analysis_queue_job`, `run_analysis_queue`, `get_analysis_queue_status`: persistent queue workflow for many videos.

Use `analyze_video` only for short, low-risk jobs where blocking is acceptable. For long videos, visual analysis, OCR, or STT work, use `create_analysis_job`.

## Job Workflow

1. Call `create_analysis_job` with source and analysis options.
2. Poll `get_job_status` every few seconds until status is `succeeded`, `failed`, or `cancelled`.
3. If `succeeded`, call `get_job_result`.
4. Extract `artifactDirectory` from the result.
5. Read `manifest.json` first, then the evidence artifacts needed for the user's request.
6. Build chapters/search only after analysis succeeds.

General analysis arguments:

```json
{
  "source": "https://example.com/video",
  "visionInstruction": "Extract transcript, representative frames, OCR, and visual evidence for answering the user's question.",
  "frames": 7,
  "frameStrategy": "scene",
  "cache": true,
  "ocr": true,
  "vision": true,
  "maxAiFrames": 5
}
```

Transcript-first arguments:

```json
{
  "source": "https://example.com/video",
  "visionInstruction": "Extract a timestamped transcript and key evidence.",
  "frames": 0,
  "cache": true
}
```

Slide, UI, code, or demo-heavy arguments:

```json
{
  "source": "https://example.com/video",
  "visionInstruction": "Extract timestamped transcript evidence, visible slide/UI text, visual context, and topic boundaries.",
  "frames": 30,
  "frameStrategy": "scene",
  "cache": true,
  "stt": true,
  "ocr": true,
  "vision": true,
  "maxAiFrames": 30
}
```

Authenticated video arguments:

```json
{
  "source": "https://example.com/private-video",
  "visionInstruction": "Extract evidence from this authenticated video.",
  "frames": 7,
  "frameStrategy": "scene",
  "cache": true,
  "browserAuth": "edge"
}
```

Use `cookies` when the user provides a cookies file path. Use `browserAuth` or `cookiesFromBrowser` only when the local browser session is expected to have legitimate access.

## Option Selection

Use these defaults unless the user says otherwise:

- `cache: true`: default for agent workflows; set `force: true` only when intentionally recomputing.
- `frames: 7`: general analysis.
- `frames: 0`: transcript-only tasks.
- `frames: 12` or more: visually dense videos.
- `frames: 30`, `frameStrategy: "scene"`: slide/demo-heavy videos.
- `frameStrategy: "scene"`: presentations, demos, UI walkthroughs, slide videos, or visually rich content.
- `frameStrategy: "every-frame"` or `everyFrame: true`: only when the user explicitly needs capped frame-by-frame inspection.
- `ocr: true`: slides, code, dashboards, diagrams, documents, or burned-in captions may be visible.
- `vision: true`: visual content matters.
- `stt: true`: captions may be absent or poor. Captions/sidecars are tried first; STT runs only when transcript extraction fails.
- `captionLanguages: ["fr", "en"]` (or `"fr,en"`): override subtitle/caption language preferences. Defaults to `["auto"]`, which merges the source's advertised manual/auto caption languages with English and YouTube live-chat. Read `metadata.json -> availableSubtitleLanguages` first to learn which languages exist for the source. Frames carry stable `id` values referenced from `ocr[*].frameId` and `vision[*].frameId`.
- `visionInstruction` and `ocrInstruction`: optional focus signals appended to the vision and OCR prompts. Both default to empty; the model already extracts every visible piece of content (vision: slide titles, bullets, code blocks, chart axes, UI controls; OCR: every readable character). Use these only to bias enumeration toward what matters for your question (e.g. `visionInstruction: "Bias toward chart axes and code"`, `ocrInstruction: "Preserve indentation in code-like text"`). Both are persisted into `evidence.json::visionInstruction` and `evidence.json::ocrInstruction` for audit. They never relax the "do not invent" guardrails.
- `framesPerMinute`: optional duration-aware sampling rate for the interval strategy. When set, the effective frame count is `max(framesPerMinute * durationMinutes, frames)`. Ignored for `scene` and `every-frame`. Use this instead of cranking `frames` when sampling a long video.
- `sceneSafetyCap`: per-run override of `frames.sceneSafetyCap` (default 2000). The scene strategy returns up to this many frames; slide grouping deduplicates. The run carries a `FRAMES_SCENE_CAP_REACHED` warning when the cap is hit, and `FRAMES_LIKELY_UNDERSAMPLED` when interval sampling without `framesPerMinute` produces fewer than 1 frame per 5 minutes.

Synthesis is your job, not Zakira.Replay's. Do not look for a `summary` flag; it does not exist. Read the evidence artifacts and produce the synthesis the user asked for.

Provider notes:

- `github-copilot` is the default provider for STT/OCR/vision.
- `openai` supports chat/image and audio transcription.
- `azure-openai` supports chat/image for OCR/vision, but Zakira.Replay STT is not implemented yet.

## Artifact Reading Order

After `get_job_result`, read artifacts from `artifactDirectory` in this order:

1. `manifest.json`: confirms produced artifacts, structured warnings, frame list, and paths.
2. `evidence.json`: structured transcript segments, frames, slides, OCR, vision, per-speaker registry (`speakers[]`), structured warnings.
3. `slides/slides.json` (also embedded in `evidence.json`): slide grouping facts (`firstSeenSeconds`, `lastSeenSeconds`, `frameIds`, `primaryFrameId`). OCR/vision run once per slide; each `OcrFrameResult` and `VisionFrameResult` carries the corresponding `slideId`.
4. `transcript.md`: readable timestamped transcript with `[Speaker Name]` prefixes when captions carried speaker tags.
5. `transcript/normalization.json` and `transcript/raw.*`: audit exact quotes when normalization matters. Speaker changes are hard boundaries.
6. `audio/chunks/chunks.json`: present only when long-audio STT was silence-chunked. Useful for branching on chunk failures (warning code `STT_CHUNK_FAILED`).
7. `ocr/{frameId}.json` and `ocr/combined.md`: structured OCR (`freeText`, `lines[]`, `tables[]`); branch on `OCR_PARSE_FALLBACK` warnings to detect prose responses.
8. `vision/{frameId}.json` and `vision/combined.md`: structured vision (`kind`, `title`, `bullets[]`, `codeBlocks[]`, `charts[]`, `uiElements[]`, `freeText`); branch on `VISION_PARSE_FALLBACK` warnings.
9. `frames/`: inspect image artifacts when visual details matter.
10. `metadata.json`: title, URL, duration, uploader metadata, `availableSubtitleLanguages`.
11. `evidence.md`: concise human-readable index of artifact paths.

Speakers in `evidence.speakers[]` carry `id` (slug, stable), optional `displayName`, `segmentCount`, `totalSeconds`, `firstSeenSeconds`, `lastSeenSeconds`. Each `transcript[*]` segment has `id` (`segment-NNNN`) and may have `speakerId` and `speakerDisplayName`. STT-derived transcripts do not carry speakers in this release.

Warnings in `manifest.json` and `evidence.json` are structured records: `{ code, message, source, severity }`. Branch on `code` (for example `TRANSCRIPT_NOT_FOUND`, `STT_NO_LLM_PROVIDER`, `STT_CHUNK_FAILED`, `OCR_PARSE_FALLBACK`, `VISION_PARSE_FALLBACK`, `PERCEPTUAL_HASH_FAILED`, `FRAMES_REMOTE_FALLBACK`) rather than fuzzy-matching the message.

## Search Workflow

Build search when the transcript is long, repeated Q&A is expected, or the user asks about specific topics:

```json
{
  "runDirectory": "<artifactDirectory>",
  "backend": "sqlite-onnx"
}
```

Then query:

```json
{
  "target": "<artifactDirectory>",
  "query": "important topic or action item",
  "backend": "auto",
  "top": 10
}
```

Backend choice:

- `json`: portable sparse TF-IDF fallback.
- `sqlite`: SQLite FTS5 keyword search.
- `sqlite-onnx`: semantic search, best for natural-language retrieval, requires ONNX model and vocabulary.

Treat search results as evidence pointers. Open the referenced artifacts before making final claims.

## Chapters Workflow

Build chapters after transcript evidence exists:

```json
{
  "runDirectory": "<artifactDirectory>",
  "minDuration": 60,
  "maxDuration": 600
}
```

Read `chapters/chapters.md` for the topic outline and `chapters/chapters.json` for structured timestamps. Chapters are pure time spans plus per-chapter evidence references; titles and prose summaries are not produced by Zakira.Replay. Generate any labels you need from the chapter's evidence yourself.

## Evidence Alignment Workflow

After chapters and slides exist, call `build_evidence_alignment` to materialise cross-modal views:

```json
{
  "runDirectory": "<artifactDirectory>"
}
```

The tool writes two files under `evidence-aligned/`:

- `by-chapter.json`: per-chapter join of `slideIds`, `transcriptSegmentIds`, `ocrFrameIds`, `visionFrameIds`, and per-speaker statistics within each chapter window.
- `by-slide.json`: per-slide join of `frameIds`, `ocr`, `vision`, `transcriptSegmentIds` spoken while the slide was visible, per-speaker statistics, and the chapters the slide overlaps.

Slide visibility windows are extended to `[slide[i].firstSeenSeconds, slide[i+1].firstSeenSeconds)`. Use `by-slide.json` to answer "which transcript segments were spoken while slide X was on screen" and `by-chapter.json` to answer "which slides and speakers appeared in chapter N". Both are pure rearrangements; no inference is added beyond the next-slide-boundary visibility heuristic.

## Clip Workflow

Extract a clip only when timestamps are known or justified by evidence:

```json
{
  "source": "https://example.com/video",
  "start": "01:20",
  "end": "02:05",
  "outputName": "key-demo"
}
```

Report the clip path and timestamp range from the returned clip artifact.

## Queue Workflow

Use the MCP queue tools for many videos or resumable local processing:

1. `enqueue_analysis_queue_job` with `source`, `queueId`, optional `jobId`, and analysis options.
2. `run_analysis_queue` with `queueId`, `concurrency`, and `retries`.
3. `get_analysis_queue_status` to report pending/running/succeeded/failed jobs.
4. Read each completed run's artifact directory before synthesizing results.

## Topic Summary And Work Items Pattern

For requests like "watch this and summarize topics and work items":

1. Use `create_analysis_job` with `frames: 30`, `frameStrategy: "scene"`, `stt: true`, `ocr: true`, `vision: true`, `cache: true`, and `maxAiFrames: 30` unless the user requests cheaper settings.
2. Poll until success and get `artifactDirectory`.
3. Call `build_chapters`.
4. Call `build_search_index` with `backend: "sqlite-onnx"` when available; use `sqlite` or `json` if ONNX is unavailable.
5. Query for `action item`, `next steps`, `todo`, `follow up`, `decision`, `owner`, `deadline`, and project terms.
6. Read `chapters/chapters.md`, `evidence.json`, `transcript.md`, and `ocr/combined.md`.
7. Synthesize the topic summary and work items yourself from these facts. Write or return the requested Markdown output. If writing a file, place it next to artifacts, usually `<artifactDirectory>/work-items.md`.

Work item format:

```markdown
- [ ] OWNER -- TASK -- DUE (or "unspecified") -- [HH:MM:SS] -- evidence: "short verbatim quote"
```

Do not invent owners or due dates. Use `unspecified` when unclear. Deduplicate repeated commitments and keep the earliest strong timestamp.

## Failure Handling

If a job fails:

- Read returned `error` and `logs`.
- For dependency failures, call `doctor` and report missing `yt-dlp`, `ffmpeg`, `ffprobe`, or ONNX model files.
- If CLI access is available and the user permits local downloads, suggest `zakira-replay deps install media` or `zakira-replay deps install onnx`.
- For provider auth failures, inspect config keys for environment variable names; never ask users to store secret values in JSON config.
- For access failures, retry only with legitimate `cookies`, `cookiesFromBrowser`, or `browserAuth`.
- If transcript is missing, rerun with `stt: true` and ensure the provider supports STT.
- If visual evidence is insufficient, rerun with more `frames`, `frameStrategy: "scene"`, `ocr: true`, or `vision: true`.
- If a previous MCP job was interrupted by server restart, create a new job with the same arguments and `cache: true`.

## Evidence Discipline

When answering:

- Lead with the answer, then cite timestamped evidence.
- Separate confirmed evidence from inference.
- Mention warnings (by `code`) that affect confidence.
- Keep transcript excerpts short unless the user asks for extensive quotes.
- Do not fabricate speakers, slide contents, UI text, numbers, decisions, or work items.
- If evidence is insufficient, state what is missing and recommend concrete MCP arguments for a rerun.
