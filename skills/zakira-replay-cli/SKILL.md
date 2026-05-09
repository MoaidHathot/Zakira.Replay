---
name: zakira-replay-cli
description: Use the Zakira.Replay command-line tool to extract durable, timestamped evidence from video URLs or local media, then summarize, answer questions, extract work items, build chapters, search evidence, or create clips from the generated artifacts.
---

# Zakira.Replay CLI Skill

Use this skill when you can run shell commands and the user asks you to analyze, summarize, inspect, search, quote, clip, or extract work items from a video.

Zakira.Replay is an evidence producer. It writes transcripts, frames, OCR, vision notes, summaries, chapters, search indexes, manifests, and queue artifacts to disk. It does not directly generate final reports. Your job is to run the CLI, read the artifacts, and produce the user's requested answer from that evidence.

## Core Rule

Never claim you watched a video directly. Base every answer on `manifest.json`, `evidence.json`, `transcript.md`, frame images, `ocr/combined.md`, `vision/combined.md`, `summary.md`, or `chapters/chapters.md`.

## When To Use

Use this skill for:

- YouTube, Vimeo, webinar, course, lecture, demo, meeting, or local media analysis.
- Requests that require transcript evidence, timestamps, visual inspection, OCR, clips, chapters, search, summaries, or work-item extraction.
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
```

`media` installs portable `yt-dlp`, `ffmpeg`, and `ffprobe` where supported. `onnx` installs the default semantic-search model files. Automatic downloads only happen when configured with `dependencies.autoDownload=true` or `search.onnx.autoDownload=true`.

Dependency path overrides, if needed:

- `ZAKIRA_REPLAY_YTDLP_PATH`
- `ZAKIRA_REPLAY_FFMPEG_PATH`
- `ZAKIRA_REPLAY_FFPROBE_PATH`
- `ZAKIRA_REPLAY_ONNX_MODEL_PATH`
- `ZAKIRA_REPLAY_ONNX_VOCAB_PATH`
- Config keys: `yt-dlp.path`, `ffmpeg.path`, `ffprobe.path`, `search.onnx.*`

Do not put secret values in JSON config. Config stores environment variable names for provider secrets.

## Recommended Analysis Commands

General evidence extraction:

```powershell
zakira-replay analyze "<url-or-file>" `
  --run-id <run-id> `
  --frames 7 `
  --frame-strategy scene `
  --ocr `
  --vision `
  --summary `
  --cache
```

Transcript-first analysis:

```powershell
zakira-replay analyze "<url-or-file>" --run-id <run-id> --frames 0 --cache
```

Slide, UI, code, or demo-heavy analysis:

```powershell
zakira-replay analyze "<url-or-file>" `
  --run-id <run-id> `
  --frames 30 `
  --frame-strategy scene `
  --ocr `
  --vision `
  --summary `
  --stt `
  --cache
```

Authenticated videos:

```powershell
zakira-replay analyze "<url>" --browser-auth edge --frames 7 --frame-strategy scene --ocr --vision --summary --cache
zakira-replay analyze "<url>" --cookies "<cookies.txt>" --frames 7 --cache
```

Always quote URLs in PowerShell, especially YouTube URLs containing `&`.

## Option Selection

Use these defaults unless the user says otherwise:

- `--cache`: include by default for LLM-backed work; use `--force` only when intentionally recomputing.
- `--frames 7`: general analysis.
- `--frames 0`: transcript-only tasks.
- `--frames 12` or more: visually dense videos.
- `--frames 30 --frame-strategy scene`: slide/demo-heavy videos.
- `--frame-strategy scene`: presentations, demos, UI walkthroughs, slide videos, or visually rich content.
- `--frame-strategy every-frame`: only when the user explicitly needs capped frame-by-frame inspection.
- `--ocr`: enable when slides, code, dashboards, diagrams, documents, or burned-in captions may be visible.
- `--vision`: enable when visual content matters.
- `--summary`: enable when the user wants high-level synthesis or downstream report generation.
- `--stt`: enable when captions may be absent or poor. Captions/sidecars are tried first; STT only runs if transcript extraction fails.

Provider notes:

- `github-copilot` is the default provider for STT/OCR/vision/summary.
- `openai` supports chat/image and audio transcription via `/audio/transcriptions`.
- `azure-openai` supports chat/image for OCR/vision/summary, but Zakira.Replay STT is not implemented yet.

## Read Command Output

After `analyze`, capture:

- `Completed run:` or `Reused run:`
- `Artifacts:` directory
- `Manifest:` path
- Any `Warnings:` lines

If the command reports `Reused run`, inspect existing artifacts before deciding whether `--force` is needed.

## Artifact Reading Order

Read artifacts in this order:

1. `manifest.json`: produced paths, warnings, run ID, frame list.
2. `evidence.json`: structured transcript, frames, OCR, vision, summary, warnings.
3. `transcript.md`: human-readable timestamped transcript.
4. `transcript/normalization.json` and `transcript/raw.*`: audit exact quotes when normalization matters.
5. `summary.md`: high-level model summary if requested.
6. `ocr/combined.md` and `vision/combined.md`: visual evidence if requested.
7. `frames/`: inspect images when layout, UI, charts, code, slides, or visual details matter.
8. `metadata.json`: title, source URL, duration, uploader metadata.
9. `evidence.md`: concise human-readable artifact summary.

## Chapters And Search

Build chapters after transcript evidence exists:

```powershell
zakira-replay chapters build runs\<run-id> --min-duration 60 --max-duration 600
```

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

1. Run slide/demo-heavy analysis with `--stt --ocr --vision --summary --frames 30 --frame-strategy scene --cache` unless the user requests cheaper settings.
2. Build chapters with `zakira-replay chapters build`.
3. Build semantic search with `zakira-replay search build --backend sqlite-onnx` when available.
4. Read `chapters/chapters.md`, `summary.md`, `evidence.json`, `transcript.md`, and `ocr/combined.md`.
5. Search for `action item`, `next steps`, `todo`, `follow up`, `decision`, `owner`, `deadline`, and relevant project terms.
6. Write the final Markdown alongside the run, usually `runs/<run-id>/work-items.md`, if the user asked for a durable output file.

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

If transcript is missing:

- Rerun with `--stt`.
- Remember Azure OpenAI STT is not implemented; use GitHub Copilot or OpenAI for STT-required runs.

If visual evidence is sparse:

- Rerun with more `--frames`, `--frame-strategy scene`, `--ocr`, or `--vision`.
- Use `--frame-strategy every-frame` only with a tight `--frames` cap.

If AI provider calls fail:

- Preserve warnings in the final answer.
- Rerun with `--force` only if recomputation is worth the cost.
- For repeated OCR/vision failures, reduce `--frames` or switch provider/model if configured.

## Evidence Discipline

When answering:

- Lead with the answer, then cite timestamped evidence.
- Separate confirmed evidence from inference.
- Mention warnings that affect confidence.
- Keep transcript excerpts short unless the user asks for extensive quotes.
- Do not fabricate speakers, slide contents, UI text, numbers, decisions, or work items.
- If evidence is insufficient, state what is missing and recommend a concrete rerun command.
