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
  --stt `
  --cache
```

Authenticated videos:

```powershell
zakira-replay analyze "<url>" --browser-auth edge --frames 7 --frame-strategy scene --ocr --vision --cache
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
- `--stt`: enable when captions may be absent or poor. Captions/sidecars are tried first; STT only runs if transcript extraction fails.
- `--caption-languages`: comma-separated language preferences for yt-dlp subtitles (e.g. `--caption-languages fr,en`). Defaults to `auto`, which unions the source's primary language, the languages with **manually uploaded** subtitles (per `info.subtitles`), English (`en`, `en.*`), and YouTube live-chat. YouTube auto-translation languages (those that appear only under `info.automatic_captions`) are intentionally **not** expanded by `auto` because they are inferences from the source, not facts about what was spoken. To opt into a specific auto-translation, pass that language explicitly (`--caption-languages es`); read `metadata.json -> availableSubtitleLanguages` first to see which languages exist (`hasManual` / `hasAuto`) for the source. Stable IDs for any frames that get extracted are written to both `frames[*].id` and `ocr[*].frameId` / `vision[*].frameId` for cross-reference.
- `--vision-instruction`: optional focus signal appended to the vision prompt. The default is empty; the model already extracts every visible piece of content (slide titles, bullets, code blocks, chart axes, UI controls). Use this only to bias enumeration order toward what matters for the orchestrator's question (e.g. `"Bias toward chart axes and code"`).
- `--ocr-instruction`: optional focus signal appended to the OCR prompt. The default is empty; the model already extracts every readable character. Use this for hints like `"Preserve indentation in code-like text"`. Both instructions are persisted into `evidence.json::visionInstruction` and `evidence.json::ocrInstruction` for audit. They never relax the "do not invent" guardrails.
- `--frames-per-minute <n>`: optional duration-aware sampling rate for the interval strategy. When set, the effective frame count is `max(framesPerMinute * durationMinutes, --frames)`. Ignored for `scene` and `every-frame`. Use it instead of cranking `--frames` when sampling a long video.
- `--scene-safety-cap <n>`: per-run override of `frames.sceneSafetyCap` (default 2000). The scene strategy returns up to this many frames; slide grouping deduplicates. When the cap is hit the run carries a `FRAMES_SCENE_CAP_REACHED` warning. The pipeline also emits `FRAMES_LIKELY_UNDERSAMPLED` if interval sampling without `--frames-per-minute` produces fewer than 1 frame per 5 minutes.

There is no `--summary` flag. Synthesis is your job, not Zakira.Replay's.

Provider notes:

- `github-copilot` is the default provider for STT/OCR/vision.
- `openai` supports chat/image and audio transcription via `/audio/transcriptions`.
- `azure-openai` supports chat/image for OCR/vision, but Zakira.Replay STT is not implemented yet.

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

Warnings in `manifest.json` and `evidence.json` are structured records: `{ code, message, source, severity }`. Branch on `code` (for example `TRANSCRIPT_NOT_FOUND`, `STT_NO_LLM_PROVIDER`, `STT_CHUNK_FAILED`, `OCR_PARSE_FALLBACK`, `VISION_PARSE_FALLBACK`, `PERCEPTUAL_HASH_FAILED`, `FRAMES_REMOTE_FALLBACK`) rather than fuzzy-matching the message.

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

## Evidence Discipline

When answering:

- Lead with the answer, then cite timestamped evidence.
- Separate confirmed evidence from inference.
- Mention warnings (by `code`) that affect confidence.
- Keep transcript excerpts short unless the user asks for extensive quotes.
- Do not fabricate speakers, slide contents, UI text, numbers, decisions, or work items.
- If evidence is insufficient, say so and recommend a concrete rerun command.
