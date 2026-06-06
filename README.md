# Zakira.Replay

**Let LLMs and AI agents "watch" video.**

LLMs cannot ingest video natively. Zakira.Replay turns **almost any video on almost any website** — YouTube, Vimeo, conference recordings (Microsoft Build, Ignite), course lectures, webinars, SharePoint Stream / Microsoft Stream, Medius / mediastream.microsoft.com players, Microsoft Teams meeting recordings, plain local `.mp4` / `.mkv` / `.webm` files, and the ~1000 other sites yt-dlp supports — into the durable, timestamped, fact-shaped artifacts an agent can actually reason over.

Instead of pretending it watched a 90-minute talk, your agent quotes specific moments with timecodes from files on disk.

## What you get

- **Transcripts.** Speaker-attributed text pulled from existing captions when the source ships them (yt-dlp for URLs, sidecar `.vtt`/`.srt` for local files, SharePoint Stream / Medius / mediastream extractors for embeds), or generated on-device with local Whisper (`--llm-provider local-whisper`) or routed to GitHub Copilot / OpenAI / Azure OpenAI when no captions exist. Long audio is silence-aware-chunked so it never hits per-request token limits. Optional local speaker diarization (`--diarize`) attributes audio when caption tags don't.
- **Frames.** Two modes: full-analysis pipeline picks representative frames at ffmpeg scene-change boundaries with perceptual-hash slide grouping (so the same slide isn't OCR'd ten times), or ad-hoc spot capture — `zakira-replay frames --at 02:34,03:10,04:55` or `--from 02:00 --to 03:00 --count 5` — for known timestamps. Frames are seek-accurate; for MSE / Shaka-based players (Microsoft Build, Medius) the pipeline ffmpeg-seeks the inline HLS URL directly so spot captures work even when the JS player won't boot headlessly.
- **Structured evidence for agents.** Every run lands in `runs/<source-slug>-<sha8>/` with `manifest.json` (pipeline timings, dependency snapshot, artifact index), `evidence.json` (timestamped transcript segments + slides + per-frame OCR + per-frame vision + per-speaker registry + structured warnings), `transcript.md`, the frame images themselves, optional chapters and cross-modal alignment views, and an optional local search index (TF-IDF, SQLite FTS5, or on-device ONNX embeddings). Every file is schema-versioned (`schemas/*.schema.json`) and machine-readable.

One .NET 10 binary ships **two surfaces** for the same pipeline: a `zakira-replay` CLI (drive from shell scripts, hosted agents, or `dnx`) and an **MCP server** (`zakira-replay mcp serve`, stdio / HTTP / SSE) so any MCP-aware agent can analyze video as a first-class tool. Local providers run fully on-device when you want air-gapped operation; cloud LLM providers plug in via `Microsoft.Extensions.AI.IChatClient` when you want stronger summarization or vision quality.

> Part of the [Zakira](https://github.com/MoaidHathot?tab=repositories&q=Zakira) project family.

## Install

Zakira.Replay ships as a .NET global tool on NuGet. Requires the **.NET 10 SDK** (or runtime).

```bash
# Install once, run anywhere (recommended for repeated use):
dotnet tool install -g Zakira.Replay
zakira-replay version

# Or run one-off without installing (.NET 10 SDK only — no install, no cleanup):
dnx Zakira.Replay version
```

System binaries (`yt-dlp`, `ffmpeg`, `ffprobe`) are opt-in. Install them upfront:

```bash
zakira-replay deps install media     # yt-dlp + ffmpeg + ffprobe (portable, per-RID)
zakira-replay doctor                  # confirm everything is found / runnable
```

…or flip `zakira-replay config set dependencies.autoDownload true` once and let Zakira.Replay fetch what it needs on first use. Local providers (OCR, Whisper STT, diarization, local vision) already auto-download their models on first run by default — no extra flag.

## Quick examples

**Analyze a YouTube video** end-to-end. Frames at scene boundaries, OCR on each unique slide, vision notes, transcript from captions — all cached so re-runs are free:

```bash
zakira-replay analyze "https://www.youtube.com/watch?v=dQw4w9WgXcQ" --ocr --vision --cache
```

**Transcribe a local meeting recording** with speaker diarization and on-device Whisper STT (no captions in the source):

```bash
zakira-replay analyze "C:\meetings\team-sync.mp4" --preset meeting --llm-provider local-whisper --allow-media-download
```

**Grab three specific frames** from a Microsoft Build session at known timestamps. Works against MSE/Shaka players that yt-dlp can't resolve:

```bash
zakira-replay frames "https://build.microsoft.com/en-US/sessions/KEY01" --at 02:34,35:12,01:47:45
```

**Extract a clip** between two timestamps:

```bash
zakira-replay clip "C:\demos\walkthrough.mp4" --start 01:20 --end 02:05 --output-name key-demo
```

**Drive Zakira.Replay from an agent.** Either invoke the CLI with structured JSON output (every long-running command honors `--output-format json` — single envelope on stdout, progress on stderr):

```bash
zakira-replay analyze "<url-or-file>" --ocr --vision --cache --output-format json
```

…or start the MCP server and let any MCP-aware client (Claude Desktop, Cursor, VS Code Copilot, hosted agent platforms) call `analyze`, `analyze-start`, `frames`, `clip`, `index-build`, `chapters-build`, etc. as native tools:

```bash
zakira-replay mcp serve                              # stdio (default; for subprocess MCP clients)
zakira-replay mcp serve --transport http --port 8765 # streamable HTTP for hosted agents
```

**Search across a whole conference** after analyzing every session into individual runs:

```bash
zakira-replay index build-conference build-2026 --runs "runs/*"
zakira-replay index query build-2026 "Foundry hosted agents" --top 10 --output-format json
```

Output for every analyze run lands in `runs/<source-slug>-<sha8>/` (the run-id is deterministic per source URL so `--cache` reuse "just works"). Inspect it:

```bash
zakira-replay runs list                              # most-recent-first
zakira-replay runs show <run-id> --output-format json # manifest body on stdout
cat runs/<run-id>/manifest.json                       # timings, dependency snapshot, artifact index
cat runs/<run-id>/transcript.md                       # speaker-attributed transcript
cat runs/<run-id>/evidence.json                       # structured timestamped facts for LLM agents
```

## Agent skills (drop these into Claude / Cursor / your agent runtime)

Reusable agent skill packages ship inside the NuGet package and the repo:

| Skill | Location | When to use |
|---|---|---|
| `zakira-replay-cli` | [`skills/zakira-replay-cli/SKILL.md`](skills/zakira-replay-cli/SKILL.md) | Agent can run shell commands. The full CLI workflow: which flags to set per source type, how to read the artifacts, how to branch on warning codes, how to cite timestamps. |
| `zakira-replay-mcp` | [`skills/zakira-replay-mcp/SKILL.md`](skills/zakira-replay-mcp/SKILL.md) | Agent is connected to `zakira-replay mcp serve`. Same workflow, but via the MCP tool surface (`analyze`, `analyze-start`, `frames`, `clip`, `chapters-build`, `index-build`, …) and `replay://` resources. |
| `zakira-replay` (router) | [`skills/zakira-replay/SKILL.md`](skills/zakira-replay/SKILL.md) | Compatibility router. Points the agent at the right focused skill above based on what surface it has access to. |
| Per-source profiles | [`skills/zakira-replay/sources/*.md`](skills/zakira-replay/sources/) | Indexed by host (SharePoint Stream, Microsoft Build / Medius, YouTube, generic). The agent looks up the URL's host and reads only the matching profile, which names the recommended capture mode, flags, expected artifacts, and known warnings for that source. |

The skills are packed into the NuGet payload under the same `skills/` paths so they're available after `dotnet tool install -g Zakira.Replay`. Examples for MCP client config and JSON-RPC job flows live alongside in `skills/zakira-replay/examples/`.

Core rule for every skill: **never claim to have watched the video.** Answer from `manifest.json`, `evidence.json`, `transcript.md`, frame images, `ocr/combined.md`, `vision/combined.md`, and `chapters/chapters.md`. Zakira.Replay produces facts; the agent synthesizes the answer.

## Where to go next

- [Commands](#commands) — the full subcommand reference (with `--output-format json` envelope shapes).
- [Defaults](#defaults) — what `zakira-replay analyze <url>` does out of the box (frame strategy, OCR provider, frame budgeting, cache key).
- [Dependency Configuration](#dependency-configuration) — pinning paths, opting in to auto-download, switching the search-embedding model.
- [Frame Capture Modes](#frame-capture-modes) — `ytdlp` vs `browser` vs `auto`, the inline-media sidestep for MSE players, SharePoint Stream / Medius caption extraction.
- [MCP Jobs](#mcp-jobs) — the full MCP tool + `replay://` resource surface for agent integration.
- [Artifact Contract](#artifact-contract) — what each file in `runs/<run-id>/` contains.
- [CHANGELOG.md](CHANGELOG.md) — full release history and per-version migration notes.

## Commands

```bash
zakira-replay doctor [--output-format json|text]
zakira-replay info [--output-format json|text]
zakira-replay version
zakira-replay analyze <url-or-file> [--preset meeting|lecture|demo|interview|raw] [--vision-instruction <text>] [--ocr-instruction <text>] [--frames <count>] [--frames-per-minute <n>] [--frame-strategy interval|scene|every-frame] [--scene-safety-cap <n>] [--llm-provider github-copilot|openai|azure-openai|ollama|local-whisper] [--ocr-provider copilot|local] [--smart-crop] [--smart-crop-profile auto|teams|zoom|webex|generic|off] [--capture-mode auto|ytdlp|browser] [--auth-profile <name>] [--stt] [--ocr] [--vision] [--diarize] [--num-speakers <n>] [--diarize-threshold <0.0-1.0>] [--caption-languages <list>] [--no-slide-grouping] [--slide-hash-distance <n>] [--run-id <id>] [--cache] [--force] [--output-format json|text]
zakira-replay transcribe <url-or-file> [--stt] [--audio] [--run-id <id>] [--cache] [--force] [--output-format json|text]
zakira-replay frames <url-or-file> [--at <ts1,ts2,...> | --from <ts> --to <ts> [--count <n>] [--strategy interval|scene]] [--max-edge <px>] [--quality <1-100>] [--phash] [--scene-safety-cap <n>] [--run-id <id>] [--output-format json|text]
zakira-replay clip <url-or-file> --start <timestamp> --end <timestamp> [--run-id <id>] [--output-name <name>] [--output-format json|text]
zakira-replay runs list [--output-format json|text]
zakira-replay runs show <run-id> [--output-format json|text]
zakira-replay runs delete <run-id> --force
zakira-replay runs export <run-id> --format md|jsonl
zakira-replay index build <run-directory> [--backend json|sqlite|sqlite-onnx] [--onnx-model <id>] [--onnx-model-kind bert|bge|e5] [--onnx-model-path <path>] [--onnx-tokenizer-path <path>]
zakira-replay index query <run-directory-or-index> <query> [--top <n>] [--backend auto|json|sqlite|sqlite-onnx] [--onnx-model <id>] [--onnx-model-kind bert|bge|e5] [--onnx-model-path <path>] [--onnx-tokenizer-path <path>]
zakira-replay chapters build <run-directory> [--min-duration <seconds>] [--max-duration <seconds>]
zakira-replay align build <run-directory>
zakira-replay discover <url> [--browser] [--output <path>]
zakira-replay batch run <manifest.json> [--output-format json|text]
zakira-replay queue enqueue <url-or-file> [analysis options] [--queue-id <id>] [--job-id <id>] [--retries <n>] [--output-format json|text]
zakira-replay queue run [--queue-id <id>] [--concurrency <n>] [--retries <n>] [--output-format json|text]
zakira-replay queue status [--queue-id <id>] [--output-format json|text]
zakira-replay llm chat "<prompt>" [--llm-provider <name>] [--model <id>] [--attach <path>]
zakira-replay deps install [yt-dlp|ffmpeg|ffprobe|onnx|ocr|whisper-model|diarization|vision|media|all] [--whisper-model tiny|base|small|medium|large-v3|large-v3-turbo] [--language <pack>] [--mode heuristic|clip|clip-caption] [--model <search-embedding-id>] [--force]
zakira-replay deps status
zakira-replay auth login <profile-name> [--url <start-url>]
zakira-replay auth init-edge-profile [--url <start-url>] [--user-data-dir <path>] [--profile-directory <name>]
zakira-replay auth list
zakira-replay auth show <profile-name>
zakira-replay auth clear <profile-name>
zakira-replay auth path [profile-name]
zakira-replay config <path|list|get|set> ...
zakira-replay mcp serve [--transport stdio|http|sse] [--port <n>]
zakira-replay completion {bash|zsh|pwsh|fish}
```

Recursive global flags (accepted on every command above): `--output-format text|json|ndjson` (replaces the 0.8.x per-command `--json` flag), `--log-file <path>`, `--log-level info|debug|trace`, `--correlation-id <string>`.

### `--output-format json` envelopes

Every command above honours `--output-format json` (`ndjson` is currently treated identically — reserved for future per-event streaming). In JSON mode, stdout is **exactly one JSON object** per invocation, and human-readable progress lines (`Run directory: …`, `Looking for sidecar subtitles…`, etc.) are routed to **stderr** so stdout stays parseable. Errors continue to land on stderr; the exit code distinguishes success (0) from failure (≥1).

`analyze`, `transcribe`, and the legacy `frames` (no `--at`/`--from`/`--to`) path emit:

```jsonc
{
  "runId": "json-analyze-<id>",
  "reused": false,                                         // true when --cache short-circuited
  "artifactDirectory": "<runs-root>/runs/<run-id>",        // absolute
  "manifestPath": "<runs-root>/runs/<run-id>/manifest.json",
  "evidencePath": "<runs-root>/runs/<run-id>/evidence.json",   // null when transcript-only / not produced
  "transcriptPath": "<runs-root>/runs/<run-id>/transcript.md", // null when --no-transcript / no captions
  "audioPath":  "<runs-root>/runs/<run-id>/audio/audio.wav",   // null when --audio not set
  "ocrPath":    "<runs-root>/runs/<run-id>/ocr/combined.md",   // null when --ocr not set
  "visionPath": "<runs-root>/runs/<run-id>/vision/combined.md",// null when --vision not set
  "frameCount": 7,
  "title": "Example Title",                                // from yt-dlp / page metadata; null for local files
  "webpageUrl": "https://example.test/video",              // null for local files
  "duration": "00:01:30",                                  // null when probe failed
  "source": "https://example.test/video",                  // verbatim input
  "warnings": [{ "code": "OCR_PARSE_FALLBACK", "message": "…", "source": "ocr", "severity": "warning" }]
}
```

`clip`, `batch run`, `queue enqueue`, and `queue run` emit their own envelopes with the same one-object-on-stdout contract — `clip` returns `{runId, artifactDirectory, clipPath, startSeconds, endSeconds, durationSeconds, warnings}`; `batch run` returns `{batchId, batchDirectory, completedAt, succeeded, failed, total, items[]}`; `queue enqueue` returns `{queueId, jobId, queueDirectory, job}`; `queue run` returns the full `AnalysisQueueRunResult` (`{queueId, queueDirectory, attempted, succeeded, failed, pending, jobs[], …}`, same shape as the persisted `last-run-result.json`). All paths are absolute.

## Defaults

Out of the box, `zakira-replay analyze <url>` (or `dnx Zakira.Replay analyze <url>`) produces:

| Knob | Default | Override |
|---|---|---|
| Frame strategy | **`interval`** (predictable, bandwidth-friendly; was `scene` through 0.13) | `--frame-strategy scene\|every-frame` |
| Frame count | **`15`** (was `500` through 0.13; lower default keeps cold runs fast) | `--frames <n>` |
| Frames per minute (interval-strategy floor) | **`12`** (`frames.perMinute` config) | `--frames-per-minute <n>`; pass `0` to disable scaling |
| Scene safety cap | **`5000`** scene frames | `--scene-safety-cap <n>` or `frames.sceneSafetyCap` config |
| Max AI frames (OCR/vision per slide cap) | **`50`** | `--max-ai-frames <n>` |
| OCR provider | **`local`** (RapidOCR via ONNX, no LLM, no network) | `--ocr-provider copilot` to route through an LLM |
| OCR model auto-download | **on** (`ocr.local.autoDownload=true`) — first OCR run silently fetches ~30 MB of PP-OCRv5 latin models from ModelScope | `zakira-replay config set ocr.local.autoDownload false`, or pre-install with `deps install ocr` |
| Run ID (when `--run-id` omitted) | **deterministic**: `<session-slug>-<sha8>` for known sources (Microsoft Build, Medius, YouTube — e.g. `brk230-1ccc2f93`), otherwise `<source-slug>-<sha8>` (capped at 40 chars). Same source URL always lands in the same run folder, so `--cache` reuse works without an explicit run-id | `--run-id <name>` to pin |
| Smart-crop | off | `--smart-crop` |
| Capture mode | **`auto`** (yt-dlp first, falls back to browser on metadata failure; known browser-only hosts — Microsoft Build / Medius / mediastream — skip yt-dlp entirely) | `--capture-mode ytdlp\|browser` to force |
| Inline-media short-circuit | **auto-on** for known Medius/Build hosts (skips the Shaka MSE duration probe, ffmpeg-seeks the inline HLS URL directly) | `--prefer-inline-media` to opt in on other browser-captured hosts |
| Verbosity | **default** (compact start/done summary; suppresses `info`-severity warnings; surfaces `warning` + `error`) | `--verbose`/`-v` for the full progress stream + every warning; `--quiet`/`-q` for errors-only |

The cache key (`runs/.cache/<sha256>.json`) is computed from the full request shape — `OcrProvider`, `SmartCrop`, `SmartCropProfile`, `CaptureMode`, and `AuthProfile` are part of it, so flipping any of these correctly invalidates prior cached runs.

## Dependency Configuration

Zakira.Replay resolves dependency paths in this order:

- Environment variable override.
- User config file.
- Portable dependency directory.
- `PATH` or known install locations.

Portable installs are opt-in. Run `zakira-replay deps install media` to install portable `yt-dlp`, `ffmpeg`, and `ffprobe` into the configured portable directory, `zakira-replay deps install onnx` to download the ONNX search model files, `zakira-replay deps install ocr [--language <pack>]` to download a RapidOCR PP-OCRv5 language pack for the local OCR provider (default `latin`; other packs: `chinese`, `english`, `korean`, `cyrillic`, `arabic`, `devanagari`, `greek`, `telugu`, `tamil`), `zakira-replay deps install whisper-model [--whisper-model <size>]` to download a Whisper ggml model for the `--llm-provider local-whisper` STT path, or `zakira-replay deps install diarization` to download the pyannote-segmentation-3.0 and 3D-Speaker ONNX models used by the `--diarize` flag. `zakira-replay deps install` defaults to `media`; use `all` to install media tools, ONNX search models, the configured OCR language pack, the default Whisper model, and the diarization models.

### Runs output directory

By default every analysis lands under `<cwd>/runs/<run-id>/`. To pin a stable location (recommended for daemon-style use), set `runs.directory` once:

```bash
zakira-replay config set runs.directory %LOCALAPPDATA%\Zakira.Replay\runs    # Windows
zakira-replay config set runs.directory ~/Library/Application\ Support/Zakira.Replay/runs    # macOS
zakira-replay config set runs.directory $XDG_DATA_HOME/Zakira.Replay/runs    # Linux
```

Environment-variable literals (e.g. `%LOCALAPPDATA%`, `$HOME`) are preserved verbatim in the JSON and expanded at read time so a single config file is portable between machines. Resolution precedence: `ZAKIRA_REPLAY_RUNS_DIRECTORY` env var > `runs.directory` config > legacy `<cwd>/runs`. `zakira-replay info` reports the resolved absolute path; `zakira-replay config get runs.directory` returns the stored literal. Same behaviour as the existing `dependencies.portableDirectory` knob.

### Auto-download flags

Zakira.Replay has six independent `autoDownload` flags. **System tools and the search model are opt-in (default `false`); local-provider models are opt-out (default `true`).** Each flag triggers fetch-on-first-use without requiring an upfront `deps install` run:

| Flag | Default | Triggers when |
|---|---|---|
| `dependencies.autoDownload` | `false` | `yt-dlp` / `ffmpeg` / `ffprobe` are needed and missing (portable `ffmpeg` is Windows-x64 only) |
| `search.onnx.autoDownload` | `false` | The `sqlite-onnx` search backend needs the configured search-embedding model (default `bge-small-en-v1.5`) |
| `ocr.local.autoDownload` | **`true`** | A local OCR run needs the RapidOCR PP-OCRv5 language pack |
| `llm.localWhisper.autoDownload` | **`true`** | `--llm-provider local-whisper` needs a Whisper ggml model |
| `diarization.autoDownload` | **`true`** | `--diarize` needs the sherpa-onnx (pyannote + 3D-Speaker) models |
| `vision.local.autoDownload` | **`true`** | `--vision-provider local` needs CLIP / Florence ONNX models |

Set any flag with `zakira-replay config set <flag> <true|false>`. To go strictly offline, flip the four local-provider flags to `false` and pre-install everything with `zakira-replay deps install all`.

The global config path is resolved in this order:

- `ZAKIRA_REPLAY_CONFIG_PATH`.
- `$XDG_CONFIG_HOME/Zakira.Replay/Zakira.Replay.json` when `XDG_CONFIG_HOME` is set.
- The platform app-data fallback, such as `%APPDATA%\Zakira.Replay\Zakira.Replay.json` on Windows.

For compatibility, on first load Zakira.Replay performs a one-time migration: if a legacy `VideoWatcher\VideoWatcher.json` (or `VideoWatcher.config` / `config.json`) sits next to the new `Zakira.Replay` directory under your config root, its contents are copied to `Zakira.Replay\Zakira.Replay.json` and the legacy file is removed.

Environment variables:

- `ZAKIRA_REPLAY_YTDLP_PATH`
- `ZAKIRA_REPLAY_FFMPEG_PATH`
- `ZAKIRA_REPLAY_FFPROBE_PATH`
- `ZAKIRA_REPLAY_EDGE_PATH`
- `ZAKIRA_REPLAY_PORTABLE_DIRECTORY`
- `ZAKIRA_REPLAY_RUNS_DIRECTORY` (pins where every `runs/<run-id>/` artifact tree lands; overrides `runs.directory` config and the legacy `<cwd>/runs` default)
- `ZAKIRA_REPLAY_ONNX_MODEL` (0.10.0+: search-embedding model id — `bge-small-en-v1.5` default, `snowflake-arctic-embed-s`, `multilingual-e5-small`, or a custom string paired with explicit paths)
- `ZAKIRA_REPLAY_ONNX_MODEL_KIND` (0.10.0+: embedding scheme override — `bert`, `bge`, or `e5`; auto-derived from the model id when unset)
- `ZAKIRA_REPLAY_ONNX_MODEL_PATH`
- `ZAKIRA_REPLAY_ONNX_TOKENIZER_PATH` (0.10.0+: tokenizer file — `vocab.txt` for BERT, `sentencepiece.bpe.model` for XLM-R)
- `ZAKIRA_REPLAY_ONNX_VOCAB_PATH` (legacy alias for `ZAKIRA_REPLAY_ONNX_TOKENIZER_PATH`; preserved for 0.9.x compatibility)
- `ZAKIRA_REPLAY_ONNX_MODEL_DIRECTORY`
- `ZAKIRA_REPLAY_ONNX_MAX_SEQUENCE_LENGTH`
- `ZAKIRA_REPLAY_ONNX_EMBEDDING_DIMENSIONS`
- `ZAKIRA_REPLAY_OCR_PROVIDER`
- `ZAKIRA_REPLAY_OCR_LANGUAGE_PACK`
- `ZAKIRA_REPLAY_OCR_MODEL_DIRECTORY`
- `ZAKIRA_REPLAY_OCR_DETECTION_MODEL_PATH`
- `ZAKIRA_REPLAY_OCR_CLASSIFICATION_MODEL_PATH`
- `ZAKIRA_REPLAY_OCR_RECOGNITION_MODEL_PATH`
- `ZAKIRA_REPLAY_OCR_DICTIONARY_PATH`
- `ZAKIRA_REPLAY_AUTH_DIRECTORY`
- `ZAKIRA_REPLAY_EDGE_USER_DATA_DIR`
- `ZAKIRA_REPLAY_LLM_PROVIDER`
- `ZAKIRA_REPLAY_OLLAMA_ENDPOINT`
- `ZAKIRA_REPLAY_OLLAMA_MODEL`
- `ZAKIRA_REPLAY_OLLAMA_VISION_MODEL`
- `OLLAMA_HOST` (Ollama's standard env var; honoured as a fallback for the endpoint)
- `ZAKIRA_REPLAY_WHISPER_MODEL_PATH`
- `ZAKIRA_REPLAY_WHISPER_MODEL_DIRECTORY`
- `ZAKIRA_REPLAY_WHISPER_MODEL_SIZE`
- `ZAKIRA_REPLAY_WHISPER_LANGUAGE`
- `ZAKIRA_REPLAY_WHISPER_THREADS`
- `ZAKIRA_REPLAY_WHISPER_AUTODOWNLOAD`
- `ZAKIRA_REPLAY_DIARIZATION_PROVIDER`
- `ZAKIRA_REPLAY_DIARIZATION_MODEL_DIRECTORY`
- `ZAKIRA_REPLAY_DIARIZATION_SEGMENTATION_MODEL_PATH`
- `ZAKIRA_REPLAY_DIARIZATION_EMBEDDING_MODEL_PATH`
- `ZAKIRA_REPLAY_DIARIZATION_NUM_SPEAKERS`
- `ZAKIRA_REPLAY_DIARIZATION_THRESHOLD`
- `ZAKIRA_REPLAY_DIARIZATION_MIN_DURATION_ON`
- `ZAKIRA_REPLAY_DIARIZATION_MIN_DURATION_OFF`
- `ZAKIRA_REPLAY_DIARIZATION_THREADS`
- `ZAKIRA_REPLAY_DIARIZATION_AUTODOWNLOAD`
- `HF_TOKEN` (optional — used by `deps install whisper-model` to lift Hugging Face download rate limits)
- `OPENAI_API_KEY`
- `OPENAI_BASE_URL`
- `OPENAI_MODEL`
- `OPENAI_TRANSCRIPTION_MODEL`
- `AZURE_OPENAI_ENDPOINT`
- `AZURE_OPENAI_API_KEY`
- `AZURE_OPENAI_DEPLOYMENT`
- `AZURE_OPENAI_MODEL`
- `AZURE_OPENAI_API_VERSION`

User config commands:

```bash
zakira-replay config path
zakira-replay config list
zakira-replay deps status
zakira-replay deps install media
zakira-replay deps install onnx
zakira-replay config set yt-dlp.path C:\tools\yt-dlp\yt-dlp.exe
zakira-replay config set ffmpeg.path C:\tools\ffmpeg\bin\ffmpeg.exe
zakira-replay config set dependencies.autoDownload true
zakira-replay config set dependencies.portableDirectory C:\tools\zakira-replay
zakira-replay config set runs.directory %LOCALAPPDATA%\Zakira.Replay\runs
zakira-replay config set search.onnx.model bge-small-en-v1.5
zakira-replay config set search.onnx.modelPath C:\models\bge-small-en-v1.5\model.onnx
zakira-replay config set search.onnx.tokenizerPath C:\models\bge-small-en-v1.5\vocab.txt
zakira-replay config set search.onnx.autoDownload true
zakira-replay config set search.onnx.modelDirectory C:\models\bge-small-en-v1.5
zakira-replay config set llm.provider openai
zakira-replay config set llm.openai.model gpt-4o-mini
zakira-replay config set llm.openai.apiKeyEnvVars OPENAI_API_KEY,WORK_OPENAI_API_KEY
zakira-replay config set llm.azureOpenAi.endpoint https://example.openai.azure.com
zakira-replay config set llm.azureOpenAi.deployment video-analysis
zakira-replay config set llm.azureOpenAi.apiKeyEnvVars AZURE_OPENAI_API_KEY,WORK_AZURE_OPENAI_API_KEY
zakira-replay config set captions.languages auto
zakira-replay config set captions.languages fr,en,live_chat
zakira-replay config set ocr.provider local
zakira-replay config set ocr.local.modelDirectory C:\models\rapidocr
zakira-replay config set ocr.local.autoDownload true
zakira-replay config set frames.sceneSafetyCap 5000
zakira-replay config set frames.perMinute 12
zakira-replay config set crop.enabled true
zakira-replay config set crop.profile auto
zakira-replay config set capture.mode auto
zakira-replay config set capture.browser.seekWaitSeconds 3
zakira-replay config set auth.directory C:\secrets\zakira-auth
zakira-replay config set auth.staleThresholdMinutes 120
zakira-replay config get yt-dlp.path
```

If the value passed to `config set` is a directory, Zakira.Replay appends the expected executable name.

## Caption Languages

Caption preferences default to `["auto"]`, which unions the source's primary language, the languages with **manually uploaded** subtitles (per yt-dlp's `info.subtitles`), English (`en`, `en.*`), and YouTube live-chat replay so an existing transcript is found whenever yt-dlp knows of one. YouTube auto-translation languages (those that appear only under `info.automatic_captions` and not under `info.subtitles`) are intentionally **not** expanded by `auto`, because they are translations inferred from the source rather than facts about what was spoken. To opt into a specific auto-translation, request it explicitly with `--caption-languages es` (CLI), `captionLanguages: ["es"]` (MCP/batch), or `zakira-replay config set captions.languages es`. The languages yt-dlp advertises for a source are written to `metadata.json` under `availableSubtitleLanguages`, with `hasManual` / `hasAuto` flags per language so orchestrators can branch on what is actually available before retrying.

## Speakers

When captions carry speaker tags, Zakira.Replay extracts them as facts, not synthesis:

- VTT voice spans `<v Speaker Name>...</v>` (and self-terminating `<v Name>` lines).
- SRT line prefixes `Speaker Name: utterance` (only when the prefix shape looks like a name).
- Bracketed prefixes `[Speaker Name] utterance`.

Each `transcript[*]` segment carries `speakerId` (slugified, stable) and `speakerDisplayName` (verbatim from the source). A per-speaker registry is written under `evidence.speakers[]` with `segmentCount`, `totalSeconds`, `firstSeenSeconds`, and `lastSeenSeconds`. Transcript normalization treats speaker changes as hard boundaries: two near-duplicate utterances by different speakers are kept separate. Speakers are never invented; segments without a recognisable tag carry `null` for both fields.

STT-derived transcripts do not carry speakers in this phase. Provider-backed diarization is out of scope for this release; the schema fields are stable so a future phase can plug in cloud or local diarization without breaking consumers.

## STT Chunking

Speech-to-text on long audio is silence-chunked before each provider call to stay under per-request size limits (for example OpenAI Whisper's 25 MB cap). Audio shorter than the configured target duration is sent in one shot. When chunking actually splits the audio:

- Boundaries snap to the centre of `ffmpeg silencedetect` windows nearest each target step, falling back to a hard cut when no usable silence exists.
- Each chunk is re-encoded as 16 kHz mono PCM under `audio/chunks/chunk-NNN.wav`.
- Per-chunk transcript responses have their timestamps shifted by the chunk's start offset so downstream consumers continue to see one continuous timeline.
- A `audio/chunks/chunks.json` artifact records chunk metadata and detected silence windows (schema `audio-chunks.schema.json`).
- Per-chunk failures are recorded as structured warnings (`STT_CHUNK_FAILED`) instead of failing the whole run.

## Slides

Frames are perceptually hashed (64-bit dHash via ffmpeg, no managed image library required) and adjacent frames within a Hamming distance threshold are grouped into slides. Slides are facts about visible-content continuity: an orchestrator can answer "when was slide X visible?" by reading `firstSeenSeconds`/`lastSeenSeconds` directly from `evidence.slides[]` (also written to `slides/slides.json`).

OCR and vision run once per slide (the slide's `primaryFrameId`), not per individual frame, so a 60-minute talk with 30 scene frames typically pays for far fewer LLM calls. Each `OcrFrameResult` and `VisionFrameResult` carries a `slideId` reference back to its slide.

Tunables:

- `slides.enabled` (default `true`) — set false to disable grouping; every frame becomes its own slide.
- `slides.hashDistance` (default `6`, range 0-64) — maximum Hamming distance between adjacent dHash values still considered the same slide.
- CLI: `--no-slide-grouping` and `--slide-hash-distance <n>`.
- MCP: `slideGrouping: false` and `slideHashDistance: <n>`.

## Frame Budgeting

`--frames N` is a per-strategy parameter, not a global density:

| Strategy | What `--frames N` produces |
|---|---|
| `interval` (default) | exactly N frames spaced evenly across the duration |
| `scene` | up to `frames.sceneSafetyCap` (default 5000) scene-cut frames; `--frames` is ignored. Slide grouping deduplicates the unbounded stream so OCR/vision cost still scales with unique slides only |
| `every-frame` | the first N decoded frames of the video (a debug/inspection tool) |

For long videos, `--frames 30` with the `interval` strategy means a frame every `duration/30` seconds — likely too sparse for a 40-minute video. Two ways to densify:

- `--frames-per-minute <n>` (CLI), `framesPerMinute` (MCP/batch). Scales the count by duration; `--frames` becomes the floor: `effective = max(framesPerMinute * durationMinutes, --frames)`. Ignored for `scene` and `every-frame`.
- `--scene-safety-cap <n>` (CLI), `sceneSafetyCap` (MCP/batch), or `frames.sceneSafetyCap` (config) raises the upper bound on scene-strategy extraction. The default 5000 is generous for typical talks and slide-heavy demos.

If a run looks undersampled (fewer than 1 frame per 5 minutes for the `interval` strategy without `--frames-per-minute` and with `frames.perMinute=0` in config), Zakira.Replay emits a `FRAMES_LIKELY_UNDERSAMPLED` warning naming the actual ratio. When the scene safety cap is reached, it emits `FRAMES_SCENE_CAP_REACHED`. Both are facts; orchestrators can branch on the codes.

## Structured OCR/Vision

OCR and vision prompts ask the model to return strict JSON. Each `OcrFrameResult.Structured` carries `{ freeText, lines[], tables[] }`; each `VisionFrameResult.Structured` carries `{ kind, title?, bullets[], codeBlocks[], charts[], uiElements[], freeText }`. When the model returns prose instead of JSON, a tolerant fallback stores the raw text under `freeText` and a structured warning (`OCR_PARSE_FALLBACK` / `VISION_PARSE_FALLBACK`) is emitted so orchestrators can branch.

Per-frame artifacts are also written for direct loading without parsing `evidence.json`:

- `ocr/{frameId}.json` — `ocr.schema.json`
- `vision/{frameId}.json` — `vision.schema.json`

## OCR Providers

OCR can run through one of two providers, selectable per-run with `--ocr-provider`:

- `copilot` (default) — routes the image through the configured LLM (GitHub Copilot, OpenAI, or Azure OpenAI) using vision-capable chat models. Produces high-quality structured OCR including the `lines[]` and `tables[]` fields when the model returns strict JSON.
- `local` — runs entirely on the local machine via [RapidOcrNet](https://github.com/BobLd/RapidOcrNet) (PP-OCRv5 latin) over `Microsoft.ML.OnnxRuntime`. No LLM call, no network at run-time, no per-frame latency cost beyond decoding and ONNX inference. Lower-fidelity than a frontier vision model (no `tables[]` reconstruction in this release) but offline and reliable.

Both providers return the same JSON shape; `OcrFrameResult.Provider` records which one produced each result. The pipeline writes the same `ocr/{frameId}.json` and `ocr/combined.md` artifacts regardless of provider.

Set the default provider once:

```bash
zakira-replay config set ocr.provider local
```

Or override per run:

```bash
zakira-replay analyze "<url>" --frames 7 --frame-strategy scene --ocr --ocr-provider local --cache
```

Install the local models (~30 MB, four files: detection ONNX, classification ONNX, recognition ONNX, character dictionary):

```bash
zakira-replay deps install ocr
zakira-replay deps status     # prints the resolved OCR model paths
```

Models are stored under `<portable-dir>/models/rapidocr-ppocrv5-latin/` by default. Override with `ocr.local.modelDirectory` in config or `ZAKIRA_REPLAY_OCR_MODEL_DIRECTORY`. Individual file paths can be overridden with `ocr.local.detectionModelPath`, `ocr.local.classificationModelPath`, `ocr.local.recognitionModelPath`, and `ocr.local.dictionaryPath` (or the corresponding `ZAKIRA_REPLAY_OCR_*` env vars).

Warning codes emitted by the local provider:

- `OCR_LOCAL_MODELS_MISSING` — one or more of the four model files were not found at resolution time. Run `deps install ocr`.
- `OCR_LOCAL_INIT_FAILED` — ONNX session construction or RapidOCR initialisation failed.
- `OCR_LOCAL_INFERENCE_FAILED` — a single frame failed to OCR; the run continues with the remaining frames.
- `OCR_UNKNOWN_PROVIDER` — the requested provider name normalised to a value that is neither `copilot` nor `local`.

`--ocr-instruction` is ignored by the local provider (the engine extracts every visible character regardless), but the instruction is still persisted to `evidence.json` and `manifest.json` for audit.

## Smart Crop (Teams/Zoom/WebEx)

Meeting-platform recordings (Teams, Zoom, WebEx, etc.) wrap slide content with UI chrome: a controls bar at the top, a participant gallery on the right, black letterbox bars, and a slide-navigation strip at the bottom. That chrome wastes 30-50% of every frame, dilutes the perceptual-hash signal used for slide grouping, and pollutes OCR output with meeting-app vocabulary. Enable smart-crop to strip it before downstream stages run:

```bash
zakira-replay analyze "C:\meetings\team-sync.mp4" --frames 12 --frame-strategy scene --ocr --vision --smart-crop
```

Or set the default once:

```bash
zakira-replay config set crop.enabled true
zakira-replay config set crop.profile auto
```

The reference algorithm (ported from the `conference-book-of-news` SKILL) runs four passes on each frame in order:

1. **Top/bottom letterbox**: trim solid black bars from the top and bottom.
2. **Controls bar**: find a fully-bright row in the first 80 px of remaining content (the meeting-app control strip) and trim past it.
3. **Participant gallery sidebar**: scan from 90 % &rarr; 60 % of the width for a thin bright strip with darker content to its left. Crop at the strip.
4. **Bottom navigation**: unconditional 25 px trim.

The cropped frame is written to `frames/<frameId>-cropped.jpg`. The `FrameArtifact` records the source dimensions, the resulting `Width`/`Height`, the `Crop` rectangle, and `OriginalPath` pointing back at the source. Downstream stages (perceptual hash, slide grouping, OCR, vision) read `Path` opaquely and automatically see the cropped frame.

Profiles (`--smart-crop-profile`):

- `auto` (default), `generic`, `teams`, `zoom`, `webex` — share the same algorithm in this release; the value is recorded on each `FrameCropBox.Source` (`smart-crop-teams`, `smart-crop-auto`, etc.) for audit and so future platform-specific tunings can branch on it.
- `off` — disable smart-crop regardless of `--smart-crop` or `crop.enabled`.

Safety: if the candidate crop would remove more than 50 % of the width or leave less than 30 % of the height, the original frame is retained and a `CROP_BAIL_OUT` (severity `info`) is emitted. This prevents the algorithm from over-cropping non-meeting content (slide-only recordings, screen captures without UI chrome, etc.).

Warning codes emitted by smart-crop:

- `CROP_IMAGE_DECODE_FAILED` — could not decode a frame (e.g. missing file).
- `CROP_BAIL_OUT` — safety threshold tripped; original frame retained.
- `CROP_PROFILE_UNKNOWN` — the requested profile name is not recognised; falls back to `auto`.
- `CROP_OUTPUT_FAILED` — failed to write the cropped JPG to disk.

## Frame Capture Modes

`--capture-mode` (or `capture.mode` in config) selects how frames are pulled out of the source:

- `ytdlp` (default) — resolve a direct media URL with `yt-dlp` and extract frames with `ffmpeg`. Works for the ~1000 sites yt-dlp supports plus local media files; cheap, fast, no browser required.
- `browser` — drive a Playwright-controlled Chromium pinned to the user's Edge install (`edge.path`) to navigate the page, click play, poll `video.duration`, seek with `video.currentTime`, and screenshot the `<video>` element at evenly-spaced timestamps. Use for sites yt-dlp can't reach: custom enterprise portals, Medius/Teams recordings, dynamic players whose URL only serves a fully-rendered SPA.
- `auto` — try yt-dlp first; if it can't resolve a direct media URL, fall back to `browser` and emit a `CAPTURE_BROWSER_FALLBACK` info-level warning so orchestrators can audit which path was used.

```bash
# Force browser capture for an authenticated SharePoint portal
zakira-replay analyze "https://corp.sharepoint.com/sites/.../watch/abc" --capture-mode browser --frames 7 --ocr --vision

# Let Zakira.Replay decide; safe to use as a default
zakira-replay analyze "https://example.com/some-video" --capture-mode auto --frames 7 --cache
```

Browser-mode tunables (config keys; CLI access is limited to `--capture-mode` for now):

- `capture.browser.playButtonSelector` — CSS or Playwright locator for the play button. When null, the client tries `video.play()` on the element matching `videoElementSelector`, then falls back to the first `button[aria-label*='play' i]`.
- `capture.browser.videoElementSelector` — CSS selector for the `<video>` element. Defaults to `video`.
- `capture.browser.seekWaitSeconds` — wait after `video.currentTime = ...` before screenshotting. The reference SKILL uses 2.5s (1.0 too fast, 2.0 mostly works, 2.5 reliable). Raise to 3.0-4.0 for HD videos or slower machines.
- `capture.browser.durationProbeTimeoutSeconds` — max wait for `video.duration` to become a finite number (defaults to 20s).
- `capture.browser.jpegQuality` — JPEG quality for screenshots written to `frames/scene-NNNN.jpg` (defaults to 90).
- `capture.browser.captureCaptions` — when true (default), attach a network listener while the page is loaded and the video is played, capturing every `.vtt` / `.srt` response into `captions/browser-NNNN.vtt` and recording an inventory at `captions/discovered.json`. When the run had no transcript otherwise (no yt-dlp captions, no sidecar, no STT), the best-language match is used to populate `transcript.md` retroactively.
- `capture.browser.maxCaptionBytes` — safety cap on the size of any single captured caption file (default 5 MiB). Larger responses are skipped with `CAPTIONS_BROWSER_NETWORK_DOWNLOAD_FAILED`.

### Browser-discovered captions

When `--capture-mode browser` (or `auto` and the browser path was used), the Playwright network interceptor watches every response that comes off the wire while the page loads and the video plays. Anything whose URL ends in `.vtt` or `.srt` (case-insensitive, after stripping query strings) is captured: the body is downloaded, deduplicated by SHA-256, and written to `captions/browser-NNNN.vtt`.

Each capture is recorded with:

- The original network URL with all query-string parameters intact (so SAS tokens and language selectors stay auditable).
- An inferred BCP-47 language code, when one can be guessed from the URL. The heuristics, tried in order, are: Microsoft Medius `Caption_<lang>.vtt` paths, generic `<sep>xx[-XX].vtt` filenames (2-letter primary), `/captions/<lang>/`-style path segments, and `?lang=` / `?hl=` / `?language=` / `?l=` / `?tlang=` query strings.
- The heuristic that produced the language tag (`url-Caption_<lang>`, `url-filename`, `url-path-segment`, `url-query-lang`, …), so false positives are easy to triage.
- Byte count, content-type, SHA-256 hash for cross-run dedupe.

The full inventory is written to `captions/discovered.json` (schema: `captions-discovered.schema.json`). When the pipeline reaches the transcript step with `transcript == null` (no yt-dlp captions, no sidecar, STT was either not requested or also failed), the best-language match is selected using the same `--caption-languages` resolution that yt-dlp uses (so `info.Language` from the source's metadata is the "main"/"original" hint), parsed via the same `SubtitleConverter`, and persisted to `transcript.md`. The `TranscriptArtifact.Kind` for these is `browser-network`.

If no captions were observed during browser playback, a `CAPTIONS_BROWSER_NETWORK_NONE` (severity `info`) is emitted so orchestrators can branch.

Warning codes specific to browser caption capture:

- `CAPTIONS_BROWSER_NETWORK_NONE` — browser capture ran but no caption response was observed.
- `CAPTIONS_BROWSER_NETWORK_DOWNLOAD_FAILED` — a single caption response failed to download (timeout, oversize body, transient Playwright error). Other captures continue.
- `CAPTIONS_BROWSER_NETWORK_PARSE_FAILED` — a captured caption file could not be parsed as VTT/SRT or parsed to zero segments. Pipeline continues with no transcript fill.

### Browser-captured media for STT fallback

When `--stt` is requested AND no inline captions were intercepted, browser capture additionally observes media-shaped responses (`video/*`, `audio/*`, HLS / DASH manifests) during playback. After the existing capture finishes, it picks the largest candidate URL and re-downloads it via the authenticated Playwright context (so SharePoint Stream's SAS-token cookies travel with the request). The downloaded file lands at `media/browser-fetched.<ext>` in the run dir; ffmpeg then extracts an audio track and Whisper STT runs as if the audio had come from yt-dlp.

This is a "fit-for-purpose" fallback, not a general media downloader:

- **Works** for sites that hand back a single addressable media URL (typical SharePoint Stream pattern when the recording was uploaded as a single MP4).
- **Does NOT work** for HLS / DASH chunked streams: audio is split across hundreds of small `.m4s` fragments with no single addressable URL. Manifest parsing + segment reassembly is out of scope for now.
- **Does NOT work** for DRM-protected streams (rare for internal corporate recordings).

The media-collection side-channel is **off by default** and only activates when **all three** of:

- `--stt` was requested (`request.UseSpeechToText == true`), AND
- The transcript step found no captions/subtitles, AND
- No audio was otherwise resolved (no yt-dlp media URL, no sidecar), AND
- **`--allow-media-download` is set** (or `capture.allowMediaDownload` is `true` in config — see [Strictly opt-in local downloads](#strictly-opt-in-local-downloads) below). `--stt` no longer implicitly authorises a local download.

When the fallback runs but no candidate URL is observed, a `CAPTURE_BROWSER_MEDIA_NO_CANDIDATE` (info) tells the orchestrator STT was skipped because the player streamed in fragments. When a candidate is found but the authenticated re-download fails (HTTP error, oversize, timeout), `CAPTURE_BROWSER_MEDIA_DOWNLOAD_FAILED` (warning) fires. When `--allow-media-download` was not set and the path would otherwise have run, `MEDIA_DOWNLOAD_DECLINED` (error) fires with an actionable message naming the flag.

Warning codes specific to browser media capture:

- `CAPTURE_BROWSER_MEDIA_DOWNLOADED` (info) — media file downloaded successfully; STT will run against it.
- `CAPTURE_BROWSER_MEDIA_NO_CANDIDATE` (info) — no single-file media URL was observed (chunked stream); STT will be skipped.
- `CAPTURE_BROWSER_MEDIA_DOWNLOAD_FAILED` (warning) — authenticated re-download failed; STT will be skipped.
- `MEDIA_DOWNLOAD_DECLINED` (error) — pipeline reached a local-download path but `--allow-media-download` is off. The message names the flag so an agent can decide whether to retry with the opt-in.

### Strictly opt-in local downloads

Every local-media-write path in the pipeline is now strictly opt-in (default off). This includes:

- `yt-dlp DownloadMediaForProcessing` in two call sites of `AnalysisPipeline` (frame extraction fallback when ffmpeg can't process the direct URL; STT fallback when no audio source is reachable).
- `FrameCaptureService.ResolveRemoteMediaAsync` (the spot-frames last-resort).
- `ClipExtractionService.ExtractAsync` (clip extraction needs a download when no direct URL is reachable; no inline-media sidestep applies).
- The browser STT collector `BrowserVideoCapture.TryDownloadBestCandidateAsync` (see above).

Three-layer opt-in resolution (highest priority wins):

1. **Per-run** `--allow-media-download` on `analyze`, `transcribe`, `frames`, `clip`, `queue enqueue`, `batch run`. Maps to `AnalyzeRequest.AllowMediaDownload` (`bool?`), `FrameCaptureRequest.AllowMediaDownload` (`bool`), `ClipExtractionRequest.AllowMediaDownload` (`bool`), batch manifest `allowMediaDownload`.
2. **Per-machine** `capture.allowMediaDownload` config key (boolean, default `false`).
3. Built-in default: `false`.

When declined, the matching site emits `MEDIA_DOWNLOAD_DECLINED` (error) with the flag name in the message and returns null / empty / throws `ReplayException` (clip). This keeps the agent contract honest: bytes that aren't on the wire are bytes the agent doesn't have to apologise for.

### Microsoft Medius / Build / Ignite — transcripts + frames without playback

Sources whose JS player is Shaka-on-MSE (Microsoft Medius, anything embedded via
`medius.studios.ms` / `medius.microsoft.com` / `medius*.event.microsoft.com`, including
`build.microsoft.com/en-US/sessions/<CODE>?source=sessions`) never boot to a finite
`video.duration` headlessly. The `MediusTranscriptInterceptor` profile (registered in
`InlineCaptionInterceptorRegistry`) sidesteps the player entirely by reading the embed
page's inline `captionsConfiguration` and `coreConfiguration` JS objects:

- **Captions** — `captionsConfiguration.languageList[]` carries up to 36 SAS-signed
  `Caption_<lang>.vtt` URLs. The interceptor downloads the preferred language(s) directly
  via the Playwright context (no playback engagement required). Verified on KEY01 (the
  2026 Microsoft Build keynote): full English transcript in seconds.
- **Frames** — `coreConfiguration.manifests.main[].manifest` carries the HLS master
  playlist URL. Exposed on `BrowserCaptureResult.InlineMediaUrl`; `FrameCaptureService`
  (for `frames --at`) and `AnalysisPipeline` (for `analyze --frames N`, see below) hand
  this URL straight to ffmpeg for seek-based extraction.

Warning codes specific to Medius:

- `CAPTURE_MEDIUS_TRANSCRIPT_DISCOVERED` (info) — caption manifest parsed; lists the
  languages advertised.
- `CAPTURE_MEDIUS_TRANSCRIPT_DOWNLOADED` (info) — one preferred-language VTT downloaded
  and persisted under `captions/medius-NNNN-<lang>.vtt`.
- `CAPTURE_MEDIUS_TRANSCRIPT_FAILED` (warning) — a discovered caption could not be
  downloaded (HTTP error or empty body).

#### Microsoft Build "InstaVOD" via `mediastream.microsoft.com`

A second, distinct player ships on `mediastream.microsoft.com` for Build sessions whose
recording was post-produced through the Harmonic media pipeline (e.g. BRK247, BRK201). Its
`onDemandUrl` looks like:

```
https://mediastream.microsoft.com/events/players/live/mvp/player.html?path=/events/<YEAR>/<EVENT>/<TENANT>/player/json/Config-<TENANT>-<CODE>IVOD.json
```

Unlike Medius, this wrapper page contains **no inline `captionsConfiguration`** — the
captions live on an HLS subtitle track that's only discoverable by:

1. Reading the `path=` query parameter and fetching the JSON config it points to.
2. Resolving `coreConfig.cdns[origin][].hostName` + `coreConfig.manifests.main[].manifest`
   into an HLS master playlist URL on `stream.event.microsoft.com`.
3. Parsing the master playlist for the `#EXT-X-MEDIA:TYPE=SUBTITLES` track.
4. Fetching the subtitle sub-playlist (typically 600–700 `Segment(N).vtt` files at 4 s each).
5. Downloading every segment in parallel and **deduplicating the rolling captions** — each
   segment shows the speaker's words being typed letter-by-letter (`ac` → `actu` → `actual`),
   so a naive concatenation produces tens of thousands of meaningless partial cues.

The `MediastreamTranscriptInterceptor` profile (registered alongside `MediusTranscriptInterceptor`)
does all five steps and emits one merged, sentence-level `mediastream-NNNN-<lang>.vtt` under
`captions/`. The dedupe takes EVERY visible line of each segment's final cue (typically 1-3
lines: bottom is the currently-growing tail, lines above are recently-completed phrases still
on-screen), runs a two-pass collapse:

1. **Screen-residence dedupe** — a line that re-appears in two adjacent segments' line sets is
   the same phrase staying on-screen as it scrolls up; emit it only on its first appearance.
2. **Prefix-extension dedupe** — a phrase that "grew letter by letter" appears as a chain of
   prefix-related entries (`but the context win` &rarr; `but the context window` &rarr;
   `but the context window is what`); collapse each chain to its longest form with a
   stretched <c>[start, end]</c> window.

`DiscoveredMediaUrl` exposes the resolved HLS master URL, so the same `frames --at` and
`analyze --frames N --prefer-inline-media` workflows that already work for Medius now work
for these sessions too.

Warning codes specific to mediastream:

- `CAPTURE_MEDIASTREAM_TRANSCRIPT_DISCOVERED` (info) — config JSON resolved to an HLS
  master URL; subtitle download about to start.
- `CAPTURE_MEDIASTREAM_TRANSCRIPT_DOWNLOADED` (info) — merged VTT written; reports
  segments-fetched / segments-listed, byte count, output path, elapsed seconds.
- `CAPTURE_MEDIASTREAM_TRANSCRIPT_FAILED` (warning) — any step failed (config fetch,
  master playlist had no subtitle entry, subtitle playlist empty, every segment fetch
  failed, dedupe produced no cues). Player iframe + frame extraction still proceed.

#### `analyze --frames N` sidestep + `--prefer-inline-media`

When the in-browser play+duration probe yields no frames AND an interceptor recovered an
inline media URL, `AnalysisPipeline.CaptureFramesAndCaptionsWithBrowserAsync` transparently
hands that URL to `ffmpeg.ExtractFramesAsync` with the request's frame strategy / scene
safety cap. The fallback emits `CAPTURE_BROWSER_FALLBACK` (info) identifying the path
(`duration-unresolved-fallback`). Transparent to callers — `analyze --frames N` against a
Build session now produces frames instead of returning an empty array.

For sources where you know the player won't boot, `--prefer-inline-media` skips the
in-browser play+duration probe entirely. A `MetadataOnly` browser probe (~3-5s, vs ~25s
for the duration timeout) navigates the page, lets interceptors observe responses, reads
the inline URL, ffmpeg-seeks the requested frames, AND downloads the inline captions in
the same pass. Falls through to the regular full-capture path when no inline URL is
discovered. Maps to `AnalyzeRequest.PreferInlineMedia` (`bool`) and batch manifest
`preferInlineMedia`.

End-to-end on KEY01 with `--frames 3 --frame-strategy interval --prefer-inline-media`:
~71 seconds, 3 real JPEGs (35:55, 01:11:50, 01:47:45), `transcript.md` 210 KB, English
VTT 217 KB. No downloads. No flag-soup.

For spot-frame capture without `analyze`, the `frames --at "00:02:00,00:22:30"` workflow
runs the same metadata-only probe under the hood and is the fastest path for "go grab the
slide at this transcript moment" agent workflows. ~18-20 s per spot frame (one HLS
keyframe download per requested timestamp).

#### Adding a new streaming-player profile

Profiles implement `IInlineCaptionInterceptor` and are registered in
`InlineCaptionInterceptorRegistry.CreateFor(request, warnings)`. Each profile exposes:

- `Name` — stable identifier for logs/warnings (e.g. `"medius"`).
- `HasDiscoveries` — true once anything relevant was observed in the page traffic.
- `OnResponse(IResponse)` — Playwright event handler; must not throw.
- `DownloadAsync(context, languagePreferences, ct)` — persist whatever was discovered
  under `captions/` and return the captured records.
- `DiscoveredMediaUrl` (default `null`) — when the profile can extract a playable URL
  from the page, expose it here so `frames --at` and the analyze sidestep can use it.

Adding a new platform (Bitmovin, Theo, JW, Brightcove, Kaltura, …) is a one-line entry
in the registry; the rest of the system iterates uniformly across all three browser-capture
exit paths.

### Autoplay-policy override

Chromium's default refuses to autoplay video with audio without a user gesture, which can
wedge some MSE-based players (the `el.play()` Promise rejects silently and the page never
boots). Override via the three-layer resolver:

1. **Per-run** `--autoplay-policy <default | no-user-gesture-required>` on
   `analyze`, `transcribe`, `queue enqueue`, `batch run`.
2. **Per-host** map in `capture.browser.autoplayPolicyByHost`. Keys are hostnames; values
   are policies. A leading `*.` is a wildcard suffix match
   (e.g. `"*.event.microsoft.com": "no-user-gesture-required"`); bare hostnames match
   exactly. Exact match beats wildcards; among wildcards, longest match wins.
3. **Global** `capture.browser.autoplayPolicy` config key. Defaults to `"default"` — no
   `--autoplay-policy` flag is passed to Chromium, so existing setups are unaffected.

Values are strings (not booleans) so future Chromium policies (e.g.
`user-gesture-required`, `document-user-activation-required`) extend without schema bumps.
Unknown values silently collapse to `"default"` so a typo never wedges the launch.

### Diagnostic capture (`--capture-debug`)

For reverse-engineering a vendor-specific player, pass `--capture-debug` to `analyze` (or `zakira-replay config set capture.browser.debug true` for a persistent default). During the existing browser-capture session, this writes a side-channel diagnostic dump under `runs/<run-id>/debug/`:

```text
runs/<run-id>/debug/
\u251c\u2500\u2500 network.log                          # JSONL: one row per response
\u251c\u2500\u2500 network.har                          # Playwright-recorded HAR (load into DevTools)
\u251c\u2500\u2500 texttracks-state.json                # snapshot of <video>.textTracks post-activation
\u2514\u2500\u2500 metadata-responses/
    \u251c\u2500\u2500 0042-3a9b2f17.json               # full body of every JSON/XML/text response
    \u251c\u2500\u2500 \u2026                                #  under `capture.browser.debugMaxBodyBytes` (default 1 MB)
    \u2514\u2500\u2500 index.json                       # URL \u2192 body file map with SHA-256s
```

The recorder doesn't affect capture behaviour \u2014 strictly side-channel, dropped silently if any individual body fails to fetch. Binary bodies (video/audio/octet-stream) are logged but not persisted to disk to keep the dump compact. Configurable cap:

```bash
zakira-replay config set capture.browser.debug true
zakira-replay config set capture.browser.debugMaxBodyBytes 5242880   # 5 MB per body
```

Useful when adding support for a new player: you can capture once, then offline-inspect what URLs the player fetches, where caption data lives in the metadata responses, and what shape it takes (inline cues, external `.vtt` URLs at a non-standard path, TTML, JSON, etc.).

## SharePoint Stream / Microsoft Stream transcripts

SharePoint Stream's player (StreamWebApp / OnePlayer) is more involved than a generic HTML5 video player and warrants a dedicated note. Three things are non-standard:

1. **Captions aren't stored in `textTracks`.** Setting `track.mode = "showing"` does nothing useful because the entries on `<video>.textTracks` are UI stubs whose `cues` arrays never populate — the actual captions live in Stream's React/SPA state.
2. **Captions aren't fetched as `.vtt`/`.srt` URLs.** The standard network interceptor sees nothing matching that pattern even when transcripts exist.
3. **Media is served as DASH-style fragmented MP4 with AES-128-CBC encryption.** Direct download of the audio is non-trivial; the existing `CAPTURE_BROWSER_MEDIA_NO_CANDIDATE` warning fires because no single-file media URL ever appears on the wire.

Zakira works around all three by recognising the Stream player's transcripts-metadata API call:

```
GET /personal/{upn}/_api/v2.X/drives/{drive-id}/items/{item-id}?select=media/transcripts,audioTracks&$expand=media/transcripts,media/audioTracks
```

The JSON response lists every transcript attached to the recording with a `temporaryDownloadUrl` per transcript. Zakira follows each URL via the authenticated Playwright context (Edge profile cookies), and tries multiple URL variants in priority order to coax out the richest format:

1. `?isformatjson=true&transcriptkey=<id>` — the exact query the Stream player itself uses. Returns the **full Microsoft Teams transcript JSON** (`$schema:transcript.json`) with `speakerDisplayName`, `speakerId`, `confidence`, `roomId`, and ISO 8601 `startOffset`/`endOffset` per entry. This is the one with speakers.
2. `?$format=json` — OData content-negotiation hint.
3. `?format=json` — non-OData fallback.
4. Plain URL — last resort, returns a stripped public WebVTT (no speakers).

When the rich JSON is obtained, Zakira converts to standard WebVTT with proper `<v Speaker>` voice spans, preserving speaker attribution through to `SubtitleConverter`. Output:

```
[00:00:06.372 - 00:00:09.572] [Liad Shiran] Hello, good morning, everyone.
[00:00:11.112 - 00:00:17.912] [Boris Forzun] Let's get started.
```

If the player happens not to make the transcripts-metadata API call itself during automation (observed varying by recording), Zakira **proactively** queries it using the `(drive-id, item-id)` harvested from any other SharePoint REST call observed on the same item (`labelPolicies`, `analytics/allTime`, etc.) — so Stream support works regardless of player behaviour.

This activates automatically — no flag needed. As long as you've initialised an Edge profile via `auth init-edge-profile` and signed into SharePoint, browser-capture against any `*.sharepoint.com/.../stream.aspx?id=...` URL produces a real, speaker-attributed transcript when one exists.

Both auto-generated Teams captions and manually uploaded transcripts work. If multiple transcripts are attached (e.g., English + machine-translated French), all are downloaded; the existing transcript-fill logic picks the best-language match for `transcript.md`.

Warning codes specific to Stream:

- `CAPTURE_STREAM_TRANSCRIPT_DISCOVERED` (info) — metadata response observed, N transcripts listed.
- `CAPTURE_STREAM_TRANSCRIPT_DOWNLOADED` (info) — per-transcript download succeeded.
- `CAPTURE_STREAM_METADATA_PARSE_FAILED` (warning) — response body wasn't recognisable JSON / `media.transcripts[]` shape.
- `CAPTURE_STREAM_TRANSCRIPT_PARSE_FAILED` (warning) — transcript body downloaded but didn't convert to WebVTT (unknown shape); raw body kept under `captions/`.

If a recording has no transcript at all (auto-captioning was disabled for the meeting, or it's not a Teams recording), STT fallback would be the next step — but Stream's audio is DRM-encrypted (DASH `urn:mpeg:dash:sea:aes128-cbc:2013`), so audio-only download requires decryption that Zakira doesn't currently ship. The `CAPTURE_BROWSER_MEDIA_NO_CANDIDATE` warning makes the gap clear and the diagnostic dump captures the DASH manifest if you want to investigate further.

## Reusing a Dedicated Edge Profile (Persistent Context)

Auth profiles store cookies as a plaintext Playwright `StorageState` JSON file. That file is portable — copy it to another machine and it works there too. Convenient, but every leaked StorageState is a complete drop-in session credential.

The alternative is to point Zakira.Replay at a **dedicated Microsoft Edge user-data-dir**. Edge stores cookies in its native SQLite, with the sensitive columns DPAPI-encrypted per-user, per-machine on Windows (Keychain on macOS, libsecret/KWallet on Linux). A leaked Edge profile is unreadable on a different machine. Cookies refresh in place during normal use, so the 1-hour StorageState refresh cycle goes away. This is the recommended approach for SharePoint Stream / Microsoft Stream / authenticated Microsoft 365 portals.

### One-command setup (per machine)

```bash
# Launch Edge against the dedicated user-data-dir, sign in interactively, close Edge.
# Zakira verifies the profile is initialised and reports the cookie path.
zakira-replay auth init-edge-profile --url https://microsofteur-my.sharepoint.com/

# Confirm Zakira sees a ready profile:
zakira-replay doctor       # \u2192 edge-profile: ready (...edge-profile, Default, ...)
```

After step 1, your browser-capture analyses **automatically** use persistent-context mode whenever the configured profile directory contains a Cookies file. No CLI flag needed:

```bash
zakira-replay analyze "https://microsofteur-my.sharepoint.com/.../stream.aspx?id=..." \
    --capture-mode browser --smart-crop --smart-crop-profile teams \
    --stt --llm-provider local-whisper --ocr --vision --vision-provider local --cache
```

### On-disk layout

```
%LOCALAPPDATA%\Zakira.Replay\edge-profile\          \u2190 user-data-dir (the directory)
\u251c\u2500\u2500 Local State                              \u2190 user-data-dir metadata
\u251c\u2500\u2500 First Run
\u2514\u2500\u2500 Default\                                 \u2190 profile sub-folder (the name)
    \u251c\u2500\u2500 Network\Cookies                       \u2190 DPAPI-encrypted SQLite
    \u251c\u2500\u2500 Login Data                            \u2190 DPAPI-encrypted (if you saved passwords)
    \u2514\u2500\u2500 \u2026
```

- `capture.browser.edgeUserDataDir` — absolute path to the user-data-dir. Stored verbatim (env-var literals like `%LOCALAPPDATA%` are preserved) so the config travels between machines; expansion happens at read time. Default: `%LOCALAPPDATA%\Zakira.Replay\edge-profile`.
- `capture.browser.edgeProfileDirectory` — sub-folder name. Default `"Default"`; only change this if you've manually created multiple profiles inside the same user-data-dir.

### Cross-machine workflow

Zakira config syncs across machines fine (the env-var literal in `edgeUserDataDir` expands per-machine). The Edge profile contents do **not** sync — DPAPI keys are per-user, per-machine, so even if you copied the directory it wouldn't be usable elsewhere. This is the property that makes the dedicated-profile approach more secure than StorageState.

On a new machine: `zakira-replay auth init-edge-profile --url <site>` once, then `zakira-replay analyze` works.

If you forget the init step, the analyze run prints:

```
[CAPTURE_BROWSER_PROFILE_NOT_INITIALIZED] (info) Edge profile at <path> is not initialized.
  Run `zakira-replay auth init-edge-profile` to sign in once per machine.
  Continuing with the StorageState path for now.
```

If you then hit an auth-gated URL:

```
[CAPTURE_BROWSER_AUTH_REQUIRED] (error) Page redirected to a sign-in URL (login.microsoftonline.com/...).
  Run `zakira-replay auth init-edge-profile --url <site>` to re-sign in and retry.
```

No more silent `CAPTURE_DURATION_UNRESOLVED` timeouts when the only real problem was an expired session.

### Failure-mode warning codes

- `CAPTURE_BROWSER_PROFILE_NOT_INITIALIZED` (info) — no Cookies file in the configured profile sub-folder. Capture falls back to StorageState/anonymous; run `auth init-edge-profile` to enable persistent-context mode.
- `CAPTURE_BROWSER_PROFILE_DIR_MISSING` (error) — explicit `edgeUserDataDir` points at a non-existent directory. Capture aborts.
- `CAPTURE_BROWSER_PROFILE_LOCKED` (error) — `SingletonLock` present inside the profile sub-folder; Edge is already using the dir. Close Edge and retry.
- `CAPTURE_BROWSER_PROFILE_LAUNCH_FAILED` (error) — `LaunchPersistentContextAsync` threw (corrupt profile, DPAPI key unavailable, incompatible Edge version). The Playwright exception message is included.
- `CAPTURE_BROWSER_AUTH_REQUIRED` (error) — post-navigation URL matched a sign-in domain. Re-init the profile.
- `CAPTURE_BROWSER_AUTH_MFA_DETECTED` (error) — page contains a Microsoft MFA challenge selector that headless capture cannot satisfy. Re-init interactively.
- `CAPTURE_PROFILE_CONFLICT` (info) — both `--auth-profile` and an initialized `edgeUserDataDir` were supplied; persistent-context wins, the StorageState profile is ignored for this run.

### Manual setup (if you'd rather not use the helper)

```bash
# Equivalent of `auth init-edge-profile`:
msedge.exe --user-data-dir="%LOCALAPPDATA%\Zakira.Replay\edge-profile" `
           --profile-directory=Default `
           --no-first-run --no-default-browser-check `
           https://microsofteur-my.sharepoint.com/
# Sign in, complete MFA, close Edge. Zakira will see the cookies on the next `doctor` / `analyze`.
```

### Security comparison vs. StorageState

| Threat | StorageState JSON (`auth login`) | Dedicated Edge profile (`auth init-edge-profile`) |
|---|---|---|
| Stolen laptop without disk encryption | Cookies fully readable as plaintext JSON, usable on any machine until expiry | Cookies unreadable (DPAPI keyed to your user+machine) |
| Accidentally committed to git / pushed to a remote | Fully usable from anywhere | Unusable on attacker's machine |
| OneDrive Known-Folder-Backup of the user profile | Plaintext JSON syncs to cloud | `%LOCALAPPDATA%` is excluded by default; encrypted contents wouldn't be useful anyway |
| Malware running as your Windows user | Direct read | DPAPI decrypts for the running user — same risk |
| Other user on the same machine | Compromised if dir is world-readable | Cookies unreadable (different DPAPI key) |
| Re-auth frequency | Every ~60 min (StorageState files expire fast) | Cookies refresh in-place during use; profile valid until Conditional Access forces re-auth |

This is standard Chromium/Edge encryption behaviour — the same DPAPI machinery that protects your daily Edge browsing.

## Evidence Alignment

`zakira-replay align build <run-directory>` (and the MCP `align` tool) emits two cross-modal views under `evidence-aligned/`. Both files share `evidence-aligned.schema.json` and are pure rearrangements of `evidence.json` (and `chapters/chapters.json` when present); no model calls are made.

- `evidence-aligned/by-chapter.json` — one entry per chapter, joining `slideIds`, `transcriptSegmentIds`, `ocrFrameIds`, `visionFrameIds`, and per-speaker statistics within the chapter window.
- `evidence-aligned/by-slide.json` — one entry per slide, joining `frameIds`, the slide's `ocr` and `vision` results, `transcriptSegmentIds` spoken while the slide was visible, per-speaker statistics over the slide window, and the chapters the slide overlaps.

Slide visibility windows are extended to `[slide[i].firstSeenSeconds, slide[i+1].firstSeenSeconds)` (with the last slide covering up to `evidence.durationSeconds`) so the answer to "which transcript segments were spoken while slide N was on screen" matches the obvious "slide N is shown until slide N+1 appears" assumption. Run `chapters build` first if you want a populated `by-chapter` view; without it, `by-chapter.json` is emitted with an empty `chapters[]` array.

For sites that require browser cookies or an authenticated session, pass through `yt-dlp` auth options:

```bash
zakira-replay analyze https://example.com/video --cookies C:\path\to\cookies.txt
zakira-replay analyze https://example.com/video --cookies-from-browser edge
zakira-replay analyze https://example.com/video --browser-auth chrome
```

LLM calls default to the GitHub Copilot SDK. The SDK uses your existing GitHub/Copilot login. The default requested model is `gpt-5.5`; if unavailable, Zakira.Replay asks the SDK for available models and falls back to a suitable model.

OpenAI and Azure OpenAI can be selected with `--llm-provider openai`, `--llm-provider azure-openai`, `ZAKIRA_REPLAY_LLM_PROVIDER`, or `llm.provider` in config. OpenAI uses chat completions for text/image work and `/audio/transcriptions` for STT. Azure OpenAI currently supports chat/image work only; audio transcription through Azure is not wired yet.

### Local Whisper STT (`--llm-provider local-whisper`)

For fully-local speech-to-text — no API key, no network, no quota — pick `local-whisper`. Zakira.Replay runs [Whisper.net](https://github.com/sandrohanea/whisper.net) (managed bindings to `whisper.cpp`) entirely on the caller's machine and emits the same Markdown timestamps as the cloud STT paths, so chunked stitching, normalisation, evidence alignment, and search work without any other changes.

`local-whisper` is **STT-only**: it has no chat/vision/OCR surface. Compose it with `--ocr-provider local` for a fully-offline run, or combine with a cloud chat provider when you still need vision/OCR. Selecting `local-whisper` for `llm ask` is rejected with a clear error.

Setup (one-time, opt-in):

```bash
# Default `small` model (~466 MB, recommended balance of accuracy and speed)
zakira-replay deps install whisper-model

# Or pick a specific size
zakira-replay deps install whisper-model --whisper-model base
zakira-replay deps install whisper-model --whisper-model large-v3-turbo
```

Sizes available (matches the whisper.cpp Hugging Face repository): `tiny`, `tiny.en`, `base`, `base.en`, `small`, `small.en`, `medium`, `medium.en`, `large-v1`, `large-v2`, `large-v3`, `large-v3-turbo`. Set `HF_TOKEN` in your environment to lift Hugging Face rate limits on large downloads.

Run STT locally:

```bash
zakira-replay analyze https://example.com/video --stt --llm-provider local-whisper --ocr-provider local
```

Configuration keys (all optional — defaults work):

| Key | Default | Purpose |
|---|---|---|
| `llm.localWhisper.modelPath` | derived from `modelSize` | Explicit ggml model path; overrides everything else |
| `llm.localWhisper.modelSize` | `small` | Size used to derive `modelPath` against the portable Whisper directory |
| `llm.localWhisper.language` | `auto` | Whisper language hint; `auto` enables built-in language detection |
| `llm.localWhisper.threads` | `null` (auto) | Native thread count |
| `llm.localWhisper.autoDownload` | `true` | First-run convenience; set false to require explicit `deps install whisper-model …` |

Environment variables (override config): `ZAKIRA_REPLAY_WHISPER_MODEL_PATH`, `ZAKIRA_REPLAY_WHISPER_MODEL_DIRECTORY`, `ZAKIRA_REPLAY_WHISPER_MODEL_SIZE`, `ZAKIRA_REPLAY_WHISPER_LANGUAGE`, `ZAKIRA_REPLAY_WHISPER_THREADS`, `ZAKIRA_REPLAY_WHISPER_AUTODOWNLOAD`.

`zakira-replay doctor` reports the resolved model path under the synthetic `whisper-model` dependency.

Native runtimes: out of the box, `Whisper.net.Runtime` (CPU) ships with the dotnet tool. For GPU acceleration (CUDA/Vulkan/CoreML/OpenVINO), follow [Whisper.net's pluggable runtime docs](https://github.com/sandrohanea/whisper.net#multiple-runtimes-support) — the loader will pick up alternative native binaries placed under the conventional `runtimes/<rid>/native/` layout.

Warning codes specific to local STT: `STT_LOCAL_MODEL_MISSING`, `STT_LOCAL_INIT_FAILED`, `STT_LOCAL_INFERENCE_FAILED`. Per-chunk failures still surface as `STT_CHUNK_FAILED`, the same way the cloud STT paths do.

### Local LLM via Ollama (`--llm-provider ollama`)

For fully-local chat and vision — no API key, no network egress — pick `ollama`. Zakira.Replay talks to a running [Ollama](https://ollama.com) daemon through [OllamaSharp](https://github.com/awaescher/OllamaSharp), which implements `Microsoft.Extensions.AI.IChatClient` natively. That makes Ollama the reference path for the `IChatClient` abstraction the codebase now exposes internally; the rest of the providers will migrate onto the same surface in subsequent releases.

`ollama` is **chat / vision only**: it does not serve audio models. Audio attachments fail fast with a pointer to `local-whisper`. Combine `--llm-provider ollama` with `--ocr-provider local` (default) and `--llm-provider local-whisper` (configured separately for STT) for an end-to-end air-gapped pipeline.

Setup (one-time, opt-in — Ollama itself is not bundled):

```bash
# 1. Install Ollama (https://ollama.com/download) — the daemon runs locally on port 11434.
# 2. Pull a model:
ollama pull qwen2.5:7b              # general chat (default)
ollama pull llama3.2-vision:11b     # vision-capable for --ocr-provider copilot / --vision

# 3. Point Zakira.Replay at it (defaults are usually fine):
zakira-replay config set llm.ollama.endpoint http://localhost:11434
zakira-replay config set llm.ollama.model qwen2.5:7b
zakira-replay config set llm.ollama.visionModel llama3.2-vision:11b
```

Run analysis through Ollama:

```bash
zakira-replay analyze https://example.com/talk --ocr --vision --llm-provider ollama --ocr-provider copilot
```

Configuration keys:

| Key | Default | Purpose |
|---|---|---|
| `llm.ollama.endpoint` | `http://localhost:11434` | HTTP endpoint of the Ollama daemon |
| `llm.ollama.model` | `qwen2.5:7b` | Chat model (matches `ollama pull` names) |
| `llm.ollama.visionModel` | `null` | Vision-capable model used when image attachments are present; falls back to `model` when null |
| `llm.ollama.timeoutSeconds` | `300` | Per-request timeout (local inference can be slow on CPU-only machines) |
| `llm.ollama.endpointEnvVars` | `[ZAKIRA_REPLAY_OLLAMA_ENDPOINT, OLLAMA_HOST]` | Env-var names checked for the endpoint override |
| `llm.ollama.modelEnvVars` | `[ZAKIRA_REPLAY_OLLAMA_MODEL]` | Env-var names checked for the chat-model override |
| `llm.ollama.visionModelEnvVars` | `[ZAKIRA_REPLAY_OLLAMA_VISION_MODEL]` | Env-var names checked for the vision-model override |

Environment variables: `ZAKIRA_REPLAY_OLLAMA_ENDPOINT`, `ZAKIRA_REPLAY_OLLAMA_MODEL`, `ZAKIRA_REPLAY_OLLAMA_VISION_MODEL`. `OLLAMA_HOST` (Ollama's own standard env var) is honoured as a secondary fallback for the endpoint.

`zakira-replay doctor` probes the daemon with a 2-second `/api/tags` request and reports the result under the synthetic `ollama` dependency.

### `IChatClient` (the LLM provider abstraction)

OpenAI and Azure OpenAI providers now use `Microsoft.Extensions.AI.IChatClient` internally — the public `ILlmProvider` surface is unchanged but the underlying transport goes through the official `OpenAI` / `Azure.AI.OpenAI` SDKs and `Microsoft.Extensions.AI.OpenAI`. Ollama already implements `IChatClient` natively. Every `ILlmProvider` also exposes an `IChatClient` view via the `AsChatClient()` extension method (Ollama returns the native client; OpenAI/Azure/Copilot go through their own implementations). This is the migration seam for future providers (Anthropic, Gemini, vLLM, llama.cpp servers) — they plug into `IChatClient` and Zakira.Replay consumes them through the same surface.

### OpenAI-compatible endpoints (OpenRouter, Together, Groq, vLLM, llama.cpp server, …)

Any OpenAI-compatible endpoint can be used through `--llm-provider openai` by overriding the base URL. No dedicated provider needed:

```bash
# OpenRouter (Claude, Gemini, Mistral, Llama, Qwen, DeepSeek, 200+ models)
export OPENAI_API_KEY=sk-or-v1-...
export OPENAI_BASE_URL=https://openrouter.ai/api/v1
zakira-replay analyze https://example.com/talk --llm-provider openai --model anthropic/claude-sonnet-4

# Groq
export OPENAI_API_KEY=gsk_...
export OPENAI_BASE_URL=https://api.groq.com/openai/v1
zakira-replay analyze https://example.com/talk --llm-provider openai --model llama-3.3-70b-versatile

# Self-hosted vLLM / llama.cpp server
export OPENAI_API_KEY=anything
export OPENAI_BASE_URL=http://localhost:8000/v1
zakira-replay analyze https://example.com/talk --llm-provider openai --model your-model-id
```

The `openai` provider routes everything through `Microsoft.Extensions.AI.OpenAI`'s `IChatClient`; the official OpenAI SDK respects `OpenAIClientOptions.Endpoint` exactly like it would for `api.openai.com`.

### Local OCR Language Packs

The local OCR provider (`--ocr-provider local`, the default) ships with the **latin** pack out of the box. RapidOCR PP-OCRv5 also publishes recognition models + dictionaries for nine other scripts; switch packs to extract non-Latin text from frames:

```bash
# Install a different pack (detection + classification are shared across packs — they download once):
zakira-replay deps install ocr --language chinese
zakira-replay deps install ocr --language korean
zakira-replay deps install ocr --language arabic

# Select which pack analysis runs use:
zakira-replay config set ocr.local.languagePack chinese
# Or per-process: ZAKIRA_REPLAY_OCR_LANGUAGE_PACK=chinese
```

Supported packs (all PP-OCRv5):

| Pack | Aliases | Covers |
|---|---|---|
| `latin` (default) | `en`, `european`, `western`, `eu` | Latin script with diacritics — French, German, Spanish, Italian, Portuguese, Polish, Vietnamese, Indonesian, etc. |
| `chinese` | `zh`, `cn`, `ch`, `simplified-chinese`, `simp`, `han` | Simplified Chinese (Han) |
| `english` | `en-only` | English with denser dictionary than Latin |
| `korean` | `ko`, `kr`, `hangul` | Korean (Hangul) |
| `cyrillic` | `ru`, `russian`, `ukrainian`, `uk`, `be`, `bg`, `sr` | Cyrillic script (Russian, Ukrainian, Belarusian, Bulgarian, Serbian) |
| `arabic` | `ar`, `fa`, `ur`, `persian`, `farsi`, `urdu` | Arabic script (Arabic, Persian, Urdu) |
| `devanagari` | `hi`, `hindi`, `mr`, `marathi`, `ne`, `nepali`, `sa`, `sanskrit` | Devanagari script |
| `greek` | `el`, `gr`, `ell` | Greek |
| `telugu` | `te`, `telegu` | Telugu (South Indian) |
| `tamil` | `ta`, `tamizh`, `tha` | Tamil (South Indian) |

Multiple packs can live side-by-side under the OCR model directory (`portable/models/rapidocr-ppocrv5-latin/` by default); switching packs is a config change, not a re-download. The detection model and classification model are shared across all packs — installing a second pack only downloads its recognition `.onnx` (~12 MB) and dictionary `.txt`.

Notes:
- **Japanese, Thai, Traditional Chinese, Georgian, Kannada** are not yet in the PP-OCRv5 release. They exist for PP-OCRv4; if you need them, set the appropriate `ocr.local.recognitionModelPath` and `ocr.local.dictionaryPath` directly and point at a downloaded v4 model — Zakira.Replay will use whatever files you point it at, regardless of pack.
- Each pack ships ~12 MB.
- `zakira-replay doctor` reports the configured pack under the `ocr-models` row.

### Local Speaker Diarization (`--diarize`)

`--diarize` runs local [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) speaker diarization over the audio that captions / STT already produced. The pipeline uses [pyannote-segmentation-3.0](https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0) for speech / speaker-change detection and a 3D-Speaker (ERes2NetV2) embedding extractor + agglomerative clustering to label each transcript segment with a `SPEAKER_NN` cluster. Everything runs on the local machine via ONNX Runtime — no network at run-time after the models are installed.

`--diarize` requires a transcript: it labels existing transcript segments, it does not transcribe. Combine with `--stt` (or rely on captions) to get speech first, then diarize on top.

Setup (one-time, opt-in — ~32 MB of models):

```bash
zakira-replay deps install diarization
```

Use:

```bash
# Auto-detect the number of speakers (threshold-based clustering, default 0.5):
zakira-replay analyze https://example.com/talk --stt --diarize

# When you know how many speakers are present, pass --num-speakers to skip the threshold:
zakira-replay analyze meeting.mp4 --stt --diarize --num-speakers 4

# Tune the clustering cutoff (lower = more speakers):
zakira-replay analyze podcast.mp4 --stt --diarize --diarize-threshold 0.35
```

After diarization, `transcript.md` is rewritten in place with `[SPEAKER_NN]` prefixes between the timestamp and the text. `TranscriptParser` picks the labels back up automatically on the next normalise pass, so the `speakers[]` registry in `evidence.json` plus the per-slide and per-chapter speaker rollups in `evidence-aligned/by-slide.json` and `by-chapter.json` are all populated without any schema changes.

Configuration keys:

| Key | Default | Purpose |
|---|---|---|
| `diarization.provider` | `sherpa-onnx` | Provider identifier; reserved for future plug-ins (pyannoteAI cloud, NeMo, etc.) |
| `diarization.modelDirectory` | portable `models/diarization/` | Where `deps install` places models and where the resolver looks for them |
| `diarization.segmentationModelPath` | derived | Explicit ONNX path; overrides the model directory |
| `diarization.embeddingModelPath` | derived | Explicit ONNX path; overrides the model directory |
| `diarization.numSpeakers` | `null` | Hard cluster count; when null, falls back to `threshold` |
| `diarization.threshold` | `0.5` | Agglomerative-clustering cosine cutoff; lower → more speakers |
| `diarization.minDurationOnSeconds` | `0.3` | Minimum speech segment duration emitted by pyannote-segmentation |
| `diarization.minDurationOffSeconds` | `0.5` | Minimum silence gap between speech segments |
| `diarization.threads` | `1` | Native thread count for sherpa-onnx inference |
| `diarization.autoDownload` | `true` | First-run convenience; set false to require explicit `deps install diarization` |

Environment variables: `ZAKIRA_REPLAY_DIARIZATION_PROVIDER`, `ZAKIRA_REPLAY_DIARIZATION_MODEL_DIRECTORY`, `ZAKIRA_REPLAY_DIARIZATION_SEGMENTATION_MODEL_PATH`, `ZAKIRA_REPLAY_DIARIZATION_EMBEDDING_MODEL_PATH`, `ZAKIRA_REPLAY_DIARIZATION_NUM_SPEAKERS`, `ZAKIRA_REPLAY_DIARIZATION_THRESHOLD`, `ZAKIRA_REPLAY_DIARIZATION_MIN_DURATION_ON`, `ZAKIRA_REPLAY_DIARIZATION_MIN_DURATION_OFF`, `ZAKIRA_REPLAY_DIARIZATION_THREADS`, `ZAKIRA_REPLAY_DIARIZATION_AUTODOWNLOAD`.

`zakira-replay doctor` reports the resolved diarization model paths and clustering configuration under the synthetic `diarization-models` dependency.

Warning codes specific to diarization: `DIARIZATION_NO_AUDIO` (no audio extracted — diarization needs the WAV the STT step uses), `DIARIZATION_NO_TRANSCRIPT` (no transcript to label), `DIARIZATION_MODELS_MISSING` (run `deps install diarization`), `DIARIZATION_INIT_FAILED` (native sherpa-onnx initialisation failed), `DIARIZATION_FAILED` (inference failed mid-run), `DIARIZATION_UNKNOWN_PROVIDER` (only `sherpa-onnx` is wired in this release). VTT `<v Speaker>` tags and SRT `Speaker:` prefixes that already labelled segments are preserved — diarization never overwrites explicit speaker attribution from captions.

Secrets themselves should stay out of JSON config. The config can store secret environment variable names instead, so agents or humans can choose which variables Zakira.Replay reads without embedding keys on disk. For example, `llm.openai.apiKeyEnvVars=OPENAI_API_KEY,WORK_OPENAI_API_KEY` tells Zakira.Replay to try those variable names for the OpenAI API key. Built-in defaults are still appended, so standard names keep working.

## Batch Manifest

```json
{
  "visionInstruction": "Focus on slide titles and chart axes.",
  "ocrInstruction": "Preserve indentation in code-like text.",
  "frames": 7,
  "useSpeechToText": true,
  "useOcr": true,
  "useVision": true,
  "items": [
    { "source": "https://example.com/video1", "runId": "video-1" },
    { "source": "C:/media/video2.mp4", "frames": 5 }
  ]
}
```

The batch runner calls the same single-video pipeline for each item and writes a batch result under `runs/`. Both instructions are optional; the pipeline's baseline already extracts everything visible from frames and every readable piece of text.

Every field maps to the matching CLI option, and can be set manifest-wide and/or overridden per item (item value wins, then manifest value, then the built-in default). Besides the basics above this includes `captureMode` (`auto` | `ytdlp` | `browser`), `authProfile`, `ocrProvider` (`copilot` | `local`), `smartCrop` / `smartCropProfile`, and diarization (`useDiarization`, `numSpeakers`, `diarizationThreshold`). For example, a transcript-only sweep of streamed conference sessions (e.g. Microsoft Build / Medius, which need browser capture) looks like:

```json
{
  "batchId": "build-2026",
  "frames": 0,
  "includeTranscript": true,
  "captureMode": "browser",
  "captionLanguages": ["en"],
  "concurrency": 4,
  "items": [
    { "source": "https://build.microsoft.com/en-US/sessions/KEY01?source=sessions", "runId": "KEY01" },
    { "source": "https://build.microsoft.com/en-US/sessions/BRK101?source=sessions", "runId": "BRK101" }
  ]
}
```

Set `"concurrency": N` (manifest-wide) or pass `--concurrency N` to `batch run` (which overrides the manifest value) to process up to N items in parallel. Default is `1` (sequential, preserving the historical behaviour). When `continueOnError` is `false` (default `true`), the first failure cancels any items not yet started; in-flight items are best-effort cancelled and dropped from the result list (only the failure that triggered the stop is recorded). Item order in `batch-result.json` always mirrors manifest order regardless of completion order.

For an evolving job set that needs to survive process restarts, use the [queue](#queue--worker-mode) instead — it adds persistence and per-job retries on top of the same concurrency model.

## Vision and OCR Steering

OCR and vision both have comprehensive baselines. Out of the box (no instruction provided) they extract:

- Vision: every distinct piece of visible content — title text, bullets, body text, code blocks, chart titles/axes/series, UI controls and labels, captioned text, diagram annotations.
- OCR: every readable piece of text in the frame, preserving line breaks, with tables surfaced when actually visible.

`--vision-instruction <text>` and `--ocr-instruction <text>` (and the equivalent `visionInstruction` / `ocrInstruction` fields in MCP and batch) are optional *focus signals* that bias enumeration order. They never relax the "do not invent" guardrails. Good steering instructions describe *what visible aspects matter*, not what to conclude:

| Good (fact-shaped) | Bad (asks for synthesis) |
|---|---|
| `Bias toward slide titles, code blocks, and chart axes.` | `Tell me which approach is better.` |
| `Identify on-screen UI controls and their labels.` | `Summarize the speaker's argument.` |
| `Capture visible commit messages and terminal output.` | `Score the slide quality.` |

Both instructions are persisted verbatim into `evidence.json` and `manifest.json` (empty string when not provided) so the audit trail records exactly how the run was framed.

## Queue / Worker Mode

For scalable CLI orchestration, Zakira.Replay includes a persistent local queue under `runs/.queue/<queue-id>/`:

```bash
zakira-replay queue enqueue https://example.com/video --queue-id research --job-id video-1 --frames 7 --cache --retries 2
zakira-replay queue status --queue-id research --json
zakira-replay queue run --queue-id research --concurrency 2 --retries 2
```

Queue state is stored in `queue.json`; the most recent worker pass writes `last-run-result.json`. Jobs move through `pending`, `running`, `succeeded`, and `failed`. If a worker stops while jobs are marked `running`, the next status/run load returns them to `pending` with a restart note so they can be retried.

`--concurrency` controls how many jobs a worker pass runs at once. `--retries` is the retry count beyond the first attempt, so `--retries 2` allows up to 3 attempts total.

If `--run-id` is provided and a completed `manifest.json` already exists, Zakira.Replay reuses that run by default. Pass `--force` to recompute it.

If `--cache` is provided without `--run-id`, Zakira.Replay computes a deterministic cache key from the source and analysis options and reuses a matching prior run. Cache entries are stored under `runs/.cache/`.

Frame extraction defaults to **`interval`** sampling: ffmpeg returns N evenly-spaced frames (`--frames`, default 15, optionally scaled by `--frames-per-minute`, default `frames.perMinute=12` from config). Use `--frame-strategy scene` for scene-change-boundary sampling (filter `select=gt(scene,0.35)`), bounded by `frames.sceneSafetyCap` (default 5000) — better for slide-heavy content but pulls the entire stream when the source is HLS. Use `--frame-strategy every-frame` or `--every-frame` for capped sequential frame extraction, where `--frames`/`--count` is the safety cap.

Clip extraction writes timestamped clips under `clips/`:

```bash
zakira-replay clip C:\media\demo.mp4 --start 01:20 --end 02:05 --output-name dashboard-demo
```

Search indexing builds over `evidence.json` transcript, OCR, vision, and warnings. The default backend is a portable JSON TF-IDF index at `search/index.json`:

```bash
zakira-replay index build runs\example-run
zakira-replay index query runs\example-run "wireguard throughput" --top 5
```

SQLite search is also available. `sqlite` builds `search/index.sqlite` with FTS5 keyword/BM25 search. `sqlite-onnx` additionally stores local ONNX embedding vectors as float32 blobs and queries with hybrid FTS plus brute-force cosine scoring:

```bash
zakira-replay index build runs\example-run --backend sqlite
zakira-replay index build runs\example-run --backend sqlite-onnx --onnx-model bge-small-en-v1.5
zakira-replay index query runs\example-run "secure tunnel performance" --backend auto --top 5
```

ONNX embedding support expects a BERT/WordPiece-style `vocab.txt` plus an ONNX model with common text inputs such as `input_ids`, `attention_mask`, and optional `token_type_ids`. Zakira.Replay does not bundle a model.

#### Cross-run / conference index

`index build-conference <id> --runs <pattern>` aggregates `evidence.json` from N completed runs into a single searchable JSON index for an entire conference / event / topic sweep, at `<runs-root>/.indexes/<conferenceId>/index.json`. Each document carries its origin run's `RunId` and the original `SourceUrl` so query results attribute each hit to a specific session and carry a deep link.

```bash
# Pull every run under the default artifact root into a conference index.
zakira-replay index build-conference build-2026 --runs "runs/*"

# Or pick specific runs (paths or globs, comma- or semicolon-separated).
zakira-replay index build-conference build-2026 --runs "runs/key01,runs/brk101,runs/brk205"

# Query by conference id; ResolveQueryTarget figures out the path under .indexes/.
zakira-replay index query build-2026 "Foundry hosted agents" --top 10 --output-format json
```

Each `SearchMatch` returned by a cross-run query carries:

- `runId` — the originating run id (e.g. `"key01"`).
- `sourceUrl` — the original session URL (`evidence.WebpageUrl ?? evidence.Source`).
- `deepLink` — a time-anchored URL the user / agent can open directly (`?t=Ns` for
  YouTube, `?nav=t=…` for SharePoint Stream, `#t=N` for everything else including Build /
  Medius).
- `timestamp`, `startSeconds`, `endSeconds` — the cue's position in the source.
- `path` — the artifact (frame / OCR snippet) the hit came from, when applicable.

Document frequency for TF-IDF is computed across the **merged corpus** (not per-run-then-merged), so per-session rare terms like a single product name rank above globally common words across the conference. Per-run ingest failures (missing `evidence.json`, unparseable JSON) are non-fatal — recorded in `SearchIndexConferenceBuildResult.Skipped[]` and reported on stdout.

#### Conference workflow (recommended for agent-driven "book of a conference")

```pwsh
# 1) Enqueue each session — host-aware defaults (auto capture → browser, inline-media
#    sidestep, captions auto) handle everything. 4 jobs in parallel below.
zakira-replay queue enqueue "https://build.microsoft.com/en-US/sessions/KEY01?source=sessions" `
    --queue-id build-2026
# ...repeat per session...

zakira-replay queue run --queue-id build-2026 --concurrency 4 --retries 2

# 2) After all runs complete, build the cross-conference index.
zakira-replay index build-conference build-2026 --runs "runs/*"

# 3) The agent queries across the whole conference; every hit comes with a deep link.
zakira-replay index query build-2026 "Maia 200 announcement" --top 10 --output-format json
```

Download a compatible local ONNX embedding model with either command:

```powershell
zakira-replay deps install onnx
# Or use the built-in installer (recommended):  zakira-replay deps install onnx --model bge-small-en-v1.5```

The 0.10.0 installer registry knows three search-embedding models out of the box: `bge-small-en-v1.5` (default, English, ~33 MB), `snowflake-arctic-embed-s` (English, ~33 MB), and `multilingual-e5-small` (~118 MB, XLM-R tokenizer for non-English transcripts). Pick one via `zakira-replay config set search.onnx.model <id>` or per-call via `--onnx-model <id>` on `index build|query`. Each model lands under `<portableDirectory>/models/<model-id>/` so the three can coexist on disk. Indexes built with one model are not query-compatible with another — the runtime emits a `SEARCH_INDEX_EMBEDDING_MISMATCH` error and recommends `index build --force` to rebuild.

Tokenization is handled by `Microsoft.ML.Tokenizers` 2.0 (BERT WordPiece for the BGE / arctic / generic-BERT family; SentencePiece BPE for the XLM-R-based multilingual-e5 family). The library auto-detects the right path based on the tokenizer file extension (`vocab.txt` vs `sentencepiece.bpe.model`), so swapping `search.onnx.model` between known ids does not require any user-side tokenizer configuration. Custom user models that come with a `tokenizer.json` should be paired with explicit `--onnx-tokenizer-path` and `--onnx-model-kind {bert|bge|e5}` to keep the prefix-and-pooling behaviour predictable.

Chapter detection builds deterministic offline lexical chapters from transcript topic shifts and duration constraints:

```bash
zakira-replay chapters build runs\example-run --min-duration 60 --max-duration 600
```

It writes `chapters/chapters.json` and `chapters/chapters.md`.

## MCP Jobs

MCP exposes both a blocking compatibility tool and non-blocking job tools (all `verb-noun` kebab-case to satisfy clients that enforce `^[a-zA-Z0-9_-]{1,128}$` on tool names):

- `analyze`: starts analysis and waits for completion. Use only for short videos.
- `analyze-start`: starts analysis in the background and returns a `jobId`.
- `analyze-status`: returns status and recent logs.
- `analyze-result`: returns the completed manifest and artifact directory.
- `analyze-cancel`: cancels a running job.
- `clip` / `frames`: extracts a timestamped video clip / ad-hoc stills.
- `index-build`: builds a local search index over a completed run. Optional `backend` values are `json`, `sqlite`, and `sqlite-onnx`; pass `onnxModel`, `onnxModelKind`, `onnxModelPath`, and `onnxTokenizerPath` to control the embedding model for `sqlite-onnx`.
- `index-query`: queries a run directory or search index. Optional `backend` values are `auto`, `json`, `sqlite`, and `sqlite-onnx`. Mismatched embedding models raise `SEARCH_INDEX_EMBEDDING_MISMATCH`.
- `chapters-build`: builds transcript-based chapters for a completed run and writes `chapters/chapters.json` plus `chapters/chapters.md`.
- `align`: builds the cross-modal `evidence-aligned/by-chapter.json` and `by-slide.json` views.
- `queue-enqueue` / `queue-run` / `queue-status`: persistent local queue for many videos.
- `discover` / `doctor`: surface dependency status and probe a page for video URLs.

Agents should prefer the job tools for long videos or LLM-backed OCR/vision work.

In addition to tools, the MCP server exposes **resources** under the `replay://` URI scheme (added in 0.9.0). Resources are readable without firing a tool call, which is the right path for agents that just need to inspect existing artifacts:

- `replay://runs` — index of every run.
- `replay://runs/{id}/{manifest|evidence|transcript|chapters}` — top-level run artifacts.
- `replay://runs/{id}/aligned/{by-chapter|by-slide}` — cross-modal alignment views.
- `replay://runs/{id}/frames/{frameId}/{ocr|vision}` — per-frame OCR / vision JSON.
- `replay://jobs/{jobId}/logs` — live log buffer of an MCP analyze job.

The MCP server supports three transports out of the box:

```bash
zakira-replay mcp serve                                  # stdio (default; Claude Desktop / Cursor / VS Code Copilot)
zakira-replay mcp serve --transport http --port 8765     # Streamable HTTP for hosted agent platforms
zakira-replay mcp serve --transport sse --port 8765      # alias for the Streamable HTTP endpoint (legacy SSE clients)
```

Stateless mode is used for the HTTP/SSE transports so multiple replicas can serve a load-balanced agent fleet.

MCP job snapshots are persisted under `runs/.mcp/jobs/` (or the configured `runs.directory`). Completed job status and results survive MCP server restarts. Jobs that were pending or running when the server stopped are restored as failed with a restart message.

## Agent Skills

Reusable agent skill packages are included in the NuGet package:

- `skills/zakira-replay-cli/SKILL.md`: CLI workflow for agents that can run shell commands.
- `skills/zakira-replay-mcp/SKILL.md`: MCP workflow for agents connected to `zakira-replay mcp serve`.
- `skills/zakira-replay/SKILL.md`: compatibility router that points agents to the focused CLI or MCP skill.
- `skills/zakira-replay/examples/mcp-client-config.json`: generic MCP stdio config.
- `skills/zakira-replay/examples/job-flow.jsonl`: raw JSON-RPC MCP job flow.
- `skills/zakira-replay/examples/prompts.md`: prompt patterns and execution notes.
- `skills/zakira-replay/examples/artifact-checklist.md`: artifact reading checklist.

Agents should load `zakira-replay-cli` when shell access is available, or `zakira-replay-mcp` when MCP tools are available. Both skills explain how to produce artifacts, inspect warnings, search evidence, build chapters, and cite timestamps without pretending to watch video directly.

## Artifact Contract

Zakira.Replay does not generate books, reports, presentations, summaries, work items, or any other synthesized output. It produces fact-shaped evidence that external orchestrators consume.

Each analyzed video run writes a folder under `runs/` containing:

- `request.json`: original source, instruction, transcript flag, frame count, and optional run ID.
- `metadata.json`: source metadata resolved from the URL or local file, including `availableSubtitleLanguages` when the source advertises any.
- `manifest.json`: stable index of produced artifacts, structured warnings, and per-stage wall-clock timings under `timings.totalSeconds` + `timings.stages.{probe,captions,audio,stt,diarization,frames,slides,ocr,vision,evidence,...}`.
- `evidence.json`: structured evidence for downstream agents/orchestrators, including per-slide grouping, per-speaker registry, and structured warnings.
- `transcript.md`: normalized timestamped transcript when captions or sidecar subtitles are available; `[Speaker Name]` prefixes are inserted when the source carries speaker tags.
- `transcript/raw.md` and `transcript/raw.json`: raw parsed transcript before normalization.
- `transcript/normalization.json`: transcript merge audit report with merge reasons and source/result segments.
- `captions/`: raw extracted subtitle files.
- `audio/`: extracted audio when requested or needed for STT.
- `audio/chunks/`: per-chunk WAV files and `chunks.json` when long audio is silence-chunked for STT.
- `frames/`: representative frame images.
- `slides/slides.json`: slide grouping facts (first/last visible per slide, frame IDs, primary frame).
- `ocr/{frameId}.json` plus `ocr/combined.md`: structured OCR result per slide primary frame.
- `vision/{frameId}.json` plus `vision/combined.md`: structured vision result per slide primary frame.
- `chapters/chapters.json` and `chapters/chapters.md`: deterministic transcript-based chapter boundaries (when built).
- `evidence-aligned/by-chapter.json` and `evidence-aligned/by-slide.json`: cross-modal alignment views (when built).
- `evidence.md`: human-readable index of the artifact paths.

Synthesis (summaries, work items, decisions, sentiment) is the responsibility of the calling orchestrator; Zakira.Replay does not produce inferences.

JSON schemas for stable machine-readable artifacts are in `schemas/`:

- `schemas/request.schema.json`
- `schemas/manifest.schema.json`
- `schemas/evidence.schema.json`
- `schemas/transcript-normalization.schema.json`
- `schemas/chapters.schema.json`
- `schemas/clip.schema.json`
- `schemas/search-index.schema.json`
- `schemas/audio-chunks.schema.json`
- `schemas/slides.schema.json`
- `schemas/ocr.schema.json`
- `schemas/vision.schema.json`
- `schemas/evidence-aligned.schema.json`
- `schemas/batch.schema.json`
- `schemas/batch-result.schema.json`
- `schemas/queue.schema.json`
- `schemas/queue-run-result.schema.json`

External orchestration can use these artifacts to build conference books, summaries, search indexes, vector stores, QA systems, clip workflows, or custom reports.

## Run Timings

Every run writes wall-clock timings to `manifest.timings`:

```json
{
  "timings": {
    "totalSeconds": 47.832,
    "stages": {
      "probe": 0.412,
      "captions": 0.821,
      "stt": 12.155,
      "diarization": 5.673,
      "frames": 8.214,
      "slides": 0.087,
      "ocr": 6.502,
      "vision": 13.819,
      "evidence": 0.089
    }
  }
}
```

Stage names are open (orchestrators must tolerate new keys); the canonical set lives in
`RunTimingStages` and is documented in `CHANGELOG.md`. Stages absent from the map did not run.
Values are wall-clock seconds rounded to milliseconds. Use these to flag slow stages, build a
"taking longer than usual" alert, or compare end-to-end runtimes across configurations (CPU
vs GPU Whisper, local-whisper vs cloud STT, etc.).

## Pre-flight: `info --output-format json`

```bash
zakira-replay info --json
```

Returns a single JSON document covering the configured LLM provider, default model, every
schema name, plus the new `resolvedDependencies` (portable directory, OCR pack, Whisper /
Ollama / diarization paths) and `capabilities` (booleans: `localOcrReady`, `localWhisperReady`,
`diarizationReady`, `ytDlpAvailable`, `ffmpegAvailable`). Orchestrators can call this once at
startup to know which optional features are wired up before issuing analysis requests.

## Development

Run the test suite with:

```bash
dotnet test Zakira.Replay.slnx
```

The ffmpeg integration test generates a tiny fixture at runtime and skips automatically when `ffmpeg` or `ffprobe` is unavailable.

## License

Zakira.Replay is released under the [MIT License](LICENSE).
