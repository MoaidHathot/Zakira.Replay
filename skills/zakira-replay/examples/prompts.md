# Zakira.Replay Prompt Examples

## Summarize A Video

User prompt:

```text
Summarize this video and include timestamps for the main claims: https://example.com/video
```

Agent behavior:

- Start `create_analysis_job` with `frames: 7`, `frameStrategy: "scene"`, and `cache: true`.
- Add `ocr: true` and `vision: true` if slides, UI, code, or diagrams matter.
- Poll until succeeded.
- Read `manifest.json`, `evidence.json`, and `transcript.md`.
- If exact transcript fidelity matters, inspect `transcript/normalization.json` and `transcript/raw.md` before quoting.
- Synthesize the timestamped summary yourself from the evidence; Zakira.Replay does not produce summaries. Mention warnings by `code`.

## Answer A Specific Question

User prompt:

```text
In this lecture, what does the speaker say about model evaluation? https://example.com/lecture
```

Agent behavior:

- Prioritize transcript extraction.
- Use `frames: 0` unless visual evidence is relevant.
- Quote or paraphrase only from transcript/evidence artifacts.
- Build/query a search index first for long transcripts, preferably `sqlite-onnx` when the local ONNX model is configured.
- If transcript is missing, rerun with `stt: true`.

MCP search flow:

```json
{"name":"build_search_index","arguments":{"runDirectory":"<artifact-directory>","backend":"sqlite-onnx"}}
{"name":"query_search_index","arguments":{"target":"<artifact-directory>","query":"model evaluation","backend":"sqlite-onnx","top":5}}
```

## Analyze Visual Content

User prompt:

```text
Review the dashboard shown in this demo and list the visible metrics: https://example.com/demo
```

Agent behavior:

- Use `frames: 12`, `frameStrategy: "scene"`, `ocr: true`, `vision: true`, and `cache: true`.
- Inspect `ocr/combined.md`, `vision/combined.md`, and frame images.
- Separate visible text from inferred meaning.

## Offline OCR (No LLM Provider)

User prompt:

```text
Extract the slide text from this conference talk. We're offline / our LLM quota is gone.
```

Agent behavior:

- Use `ocrProvider: "local"` to run RapidOCR (PP-OCRv5 latin) entirely on-device via ONNX. No LLM, no network at run-time after the models are installed.
- The user must run `zakira-replay deps install ocr` once before the first local-OCR run; without the models the run emits `OCR_LOCAL_MODELS_MISSING`.
- Combine with `frameStrategy: "scene"` and `frames: 30` for slide-heavy content.
- Tradeoff to mention: lower fidelity than a frontier vision model on complex layouts; no `tables[]` reconstruction.

```json
{
  "name": "create_analysis_job",
  "arguments": {
    "source": "https://example.com/conference-talk",
    "visionInstruction": "Extract slide text and code blocks.",
    "frames": 30,
    "frameStrategy": "scene",
    "ocr": true,
    "ocrProvider": "local",
    "cache": true
  }
}
```

## Teams / Zoom / WebEx Meeting Recording

User prompt:

```text
Analyze this Teams meeting recording and pull out the slide content.
```

Agent behavior:

- Use `smartCrop: true` (with default `smartCropProfile: "auto"`) so the meeting-app UI chrome is removed before perceptual hashing, OCR, and vision. This dramatically improves slide grouping (the gallery sidebar no longer dilutes the dHash) and removes meeting-app vocabulary from OCR text.
- Combine with `frameStrategy: "scene"` so only slide-change frames are kept.
- Local OCR works well for slide text; consider `ocrProvider: "local"`.

```json
{
  "name": "create_analysis_job",
  "arguments": {
    "source": "C:\\meetings\\team-sync.mp4",
    "visionInstruction": "Extract the slide content.",
    "frames": 12,
    "frameStrategy": "scene",
    "smartCrop": true,
    "ocr": true,
    "ocrProvider": "local",
    "cache": true
  }
}
```

## Sites yt-dlp Cannot Reach

User prompt:

```text
Analyze this video on our internal training portal: https://corp.example.com/training/abc
```

Agent behavior:

- yt-dlp does not know about custom enterprise portals. Set `captureMode: "browser"` to drive Playwright instead.
- For SSO-gated sources, the user must first run `zakira-replay auth login <profile-name>` on their machine (interactive, browser opens visibly). Then pass `authProfile: "<profile-name>"`.
- The browser-network interceptor watches for `.vtt`/`.srt` responses during playback; if the page exposes captions, they're picked up and used to populate `transcript.md` automatically (look for `captions/discovered.json`).
- Smart-crop and local OCR still work in browser-capture mode.

```json
{
  "name": "create_analysis_job",
  "arguments": {
    "source": "https://corp.example.com/training/abc",
    "visionInstruction": "Extract slide content from this internal training video.",
    "frames": 7,
    "captureMode": "browser",
    "authProfile": "corp-sso",
    "ocr": true,
    "ocrProvider": "local",
    "smartCrop": true,
    "cache": true
  }
}
```

If the source might or might not be yt-dlp-reachable, use `captureMode: "auto"` — the pipeline tries yt-dlp first and falls back to browser capture, emitting `CAPTURE_BROWSER_FALLBACK` so you know which path produced the artifacts.

## Microsoft Ignite / MVP Summit / Build Recordings

These are hosted on Microsoft Medius. The page-side player fetches `Caption_en-US.vtt` (and other-language variants) as plain HTTP responses during initialisation, so the browser-network interceptor catches them automatically.

User prompt:

```text
Summarize this Ignite session: https://medius.studios.ms/Embed/video-12345
```

Agent behavior:

- The user must first sign in to Medius interactively via `zakira-replay auth login ignite-2026 --url https://medius.studios.ms/`.
- Then pass `captureMode: "browser"` and `authProfile: "ignite-2026"`. Captions arrive automatically through the network interceptor; no need for any Medius-specific code path.

```json
{
  "name": "create_analysis_job",
  "arguments": {
    "source": "https://medius.studios.ms/Embed/video-12345",
    "visionInstruction": "Extract slide titles, bullets, code blocks, and demo content.",
    "frames": 30,
    "frameStrategy": "scene",
    "captureMode": "browser",
    "authProfile": "ignite-2026",
    "ocr": true,
    "vision": true,
    "smartCrop": false,
    "cache": true
  }
}
```

## Authenticated Video (yt-dlp Cookies)

User prompt:

```text
Analyze this course video. I am logged into it in Edge: https://example.com/course/video
```

Agent behavior:

- Try `browserAuth: "edge"` first (yt-dlp pulls cookies from the local Edge profile).
- If yt-dlp still cannot reach the source, escalate to `captureMode: "browser"` with an `authProfile` (the user creates one with `zakira-replay auth login`).

## Batch Orchestration

User prompt:

```text
Analyze all videos in this manifest and make study notes from the evidence.
```

Agent behavior:

- Use `zakira-replay batch run <manifest.json>` if working through CLI.
- Use MCP jobs one-by-one if the orchestrator needs progress control.
- After artifacts are ready, synthesize study notes yourself from each `evidence.json` and `transcript.md`.

## Build Chapters

User prompt:

```text
Create chapter markers for this video and include supporting evidence.
```

Agent behavior:

- Analyze the video with transcript extraction.
- Call `build_chapters` with the completed run directory.
- Read `chapters/chapters.json` and cite chapter evidence timestamps. Generate any per-chapter labels yourself; chapters carry pure time spans plus evidence references, no titles or prose summaries.

## When LLM-Backed OCR Hangs

If an `ocr: true` run with the default `ocrProvider: "copilot"` repeatedly times out (the GitHub Copilot stdio session can stall as an SWE agent loop on image attachments), recommend the user retry with `ocrProvider: "local"`. The RapidOCR path has no LLM session, no agent loop, and no per-frame timeout risk.
