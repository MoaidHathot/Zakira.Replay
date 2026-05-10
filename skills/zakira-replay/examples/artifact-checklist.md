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
- If transcript is absent and audio matters, rerun with `stt: true`. Long audio is silence-chunked automatically; chunk metadata lands under `audio/chunks/chunks.json` when chunking actually fired, and per-chunk failures appear as `STT_CHUNK_FAILED` warnings.

## Visual Evidence

- Read `ocr/combined.md` for visible text from frames.
- Read `vision/combined.md` for visual descriptions.
- Inspect frame files when the user asks about layout, diagrams, UI, code, charts, or visual details.
- If frames are sparse, rerun with more `frames` or `frameStrategy: "scene"`.

## Search Evidence

- Build `search/index.json` for repeated Q&A over a run.
- Query the index before reading the full transcript when the user asks about a specific topic.
- Treat search matches as pointers into evidence, not final answers by themselves.

## Clips

- Use clip extraction only when start/end timestamps are known or can be justified from artifacts.
- Save clip paths from `clip.json` and report them with the timestamp range.

## Warnings

- Warnings are structured records: `{ code, message, source, severity }`.
- Branch on `code` (for example `TRANSCRIPT_NOT_FOUND`, `STT_NO_LLM_PROVIDER`, `FRAMES_REMOTE_FALLBACK`, `FRAMES_LIKELY_UNDERSAMPLED`, `FRAMES_SCENE_CAP_REACHED`, `OCR_PARSE_FALLBACK`, `VISION_PARSE_FALLBACK`, `PERCEPTUAL_HASH_FAILED`) instead of fuzzy-matching messages.
- Treat missing captions, missing media URL, failed OCR, failed vision, fallback downloads, and undersampled frame coverage as confidence modifiers.

## Response Quality

- Cite timestamps where possible.
- Keep raw transcript excerpts short unless the user asks for extensive quotes.
- Do not invent speaker names, slides, charts, or numbers not present in artifacts.
- If evidence is insufficient, say so and recommend a concrete rerun option.
