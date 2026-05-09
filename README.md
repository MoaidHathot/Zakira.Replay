# Zakira.Replay

Zakira.Replay is a .NET 10 CLI/global tool and MCP server for turning video sources into timestamped evidence that LLM agents can analyze.

It is part of the **Zakira** family of agent-cognition primitives:

- [Zakira.Imprint](https://github.com/MoaidHathot/Zakira.Imprint) — ship AI Skills, custom instructions, and MCP server configs alongside NuGet packages.
- [Zakira.Recall](https://www.nuget.org/packages/Zakira.Recall) — local CLI and MCP server for web search, fetch, and research.
- [Zakira.Exchange](https://www.nuget.org/packages/Zakira.Exchange) — MCP server and CLI for AI agent memory storage and semantic search.
- **Zakira.Replay** — turn videos into replayable evidence (this package).

The first implementation focuses on durable extraction primitives:

- Existing captions/subtitles via `yt-dlp` for URLs.
- Sidecar `.vtt`/`.srt` subtitles for local media files.
- Audio and representative frame extraction via `ffmpeg`/`ffprobe`.
- Optional GitHub Copilot SDK analysis for audio transcription, OCR, frame vision, and evidence summaries.
- Artifact folders with `manifest.json`, `metadata.json`, `evidence.json`, `transcript.md`, frames, and `evidence.md`.
- CLI and MCP stdio entrypoints.

Dependencies are not installed automatically unless explicitly configured. Missing dependencies fail with a clear error.

## Commands

```bash
zakira-replay doctor [--json]
zakira-replay info [--json]
zakira-replay version
zakira-replay analyze <url-or-file> [--instruction <text>] [--frames <count>] [--frame-strategy interval|scene|every-frame] [--llm-provider github-copilot|openai|azure-openai] [--stt] [--ocr] [--vision] [--summary] [--run-id <id>] [--cache] [--force]
zakira-replay transcribe <url-or-file> [--stt] [--audio] [--run-id <id>] [--cache] [--force]
zakira-replay frames <url-or-file> [--count <count>] [--frame-strategy interval|scene|every-frame] [--ocr] [--vision] [--run-id <id>] [--cache] [--force]
zakira-replay clip <url-or-file> --start <timestamp> --end <timestamp> [--run-id <id>] [--output-name <name>]
zakira-replay search build <run-directory> [--backend json|sqlite|sqlite-onnx]
zakira-replay search query <run-directory-or-index> <query> [--top <n>] [--backend auto|json|sqlite|sqlite-onnx]
zakira-replay chapters build <run-directory> [--min-duration <seconds>] [--max-duration <seconds>]
zakira-replay discover <url> [--browser] [--output <path>]
zakira-replay batch run <manifest.json>
zakira-replay queue enqueue <url-or-file> [analysis options] [--queue-id <id>] [--job-id <id>] [--retries <n>]
zakira-replay queue run [--queue-id <id>] [--concurrency <n>] [--retries <n>]
zakira-replay queue status [--queue-id <id>] [--json]
zakira-replay deps install [yt-dlp|ffmpeg|ffprobe|onnx|media|all] [--force]
zakira-replay deps path
zakira-replay config <path|list|get|set> ...
zakira-replay mcp serve
```

## Dependency Configuration

Zakira.Replay resolves dependency paths in this order:

- Environment variable override.
- User config file.
- Portable dependency directory.
- `PATH` or known install locations.

Portable installs are opt-in. Run `zakira-replay deps install media` to install portable `yt-dlp`, `ffmpeg`, and `ffprobe` into the configured portable directory, or run `zakira-replay deps install onnx` to download the ONNX search model files. `zakira-replay deps install` defaults to `media`; use `all` to install media tools and ONNX model files.

To allow on-demand downloads when a dependency is first required, set `dependencies.autoDownload=true`. To allow ONNX model download when `sqlite-onnx` search needs model files, set `search.onnx.autoDownload=true`. Both are `false` by default.

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
- `ZAKIRA_REPLAY_ONNX_MODEL_PATH`
- `ZAKIRA_REPLAY_ONNX_VOCAB_PATH`
- `ZAKIRA_REPLAY_ONNX_MODEL_DIRECTORY`
- `ZAKIRA_REPLAY_ONNX_MODEL_FILE`
- `ZAKIRA_REPLAY_ONNX_MAX_SEQUENCE_LENGTH`
- `ZAKIRA_REPLAY_ONNX_EMBEDDING_DIMENSIONS`
- `ZAKIRA_REPLAY_LLM_PROVIDER`
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
zakira-replay deps path
zakira-replay deps install media
zakira-replay deps install onnx
zakira-replay config set yt-dlp.path C:\tools\yt-dlp\yt-dlp.exe
zakira-replay config set ffmpeg.path C:\tools\ffmpeg\bin\ffmpeg.exe
zakira-replay config set dependencies.autoDownload true
zakira-replay config set dependencies.portableDirectory C:\tools\zakira-replay
zakira-replay config set search.onnx.modelPath C:\models\embedding.onnx
zakira-replay config set search.onnx.vocabularyPath C:\models\vocab.txt
zakira-replay config set search.onnx.autoDownload true
zakira-replay config set search.onnx.modelDirectory C:\models\all-MiniLM-L6-v2
zakira-replay config set llm.provider openai
zakira-replay config set llm.openai.model gpt-4o-mini
zakira-replay config set llm.openai.apiKeyEnvVars OPENAI_API_KEY,WORK_OPENAI_API_KEY
zakira-replay config set llm.azureOpenAi.endpoint https://example.openai.azure.com
zakira-replay config set llm.azureOpenAi.deployment video-analysis
zakira-replay config set llm.azureOpenAi.apiKeyEnvVars AZURE_OPENAI_API_KEY,WORK_AZURE_OPENAI_API_KEY
zakira-replay config get yt-dlp.path
```

If the value passed to `config set` is a directory, Zakira.Replay appends the expected executable name.

For sites that require browser cookies or an authenticated session, pass through `yt-dlp` auth options:

```bash
zakira-replay analyze https://example.com/video --cookies C:\path\to\cookies.txt
zakira-replay analyze https://example.com/video --cookies-from-browser edge
zakira-replay analyze https://example.com/video --browser-auth chrome
```

LLM calls default to the GitHub Copilot SDK. The SDK uses your existing GitHub/Copilot login. The default requested model is `gpt-5.5`; if unavailable, Zakira.Replay asks the SDK for available models and falls back to a suitable model.

OpenAI and Azure OpenAI can be selected with `--llm-provider openai`, `--llm-provider azure-openai`, `ZAKIRA_REPLAY_LLM_PROVIDER`, or `llm.provider` in config. OpenAI uses chat completions for text/image work and `/audio/transcriptions` for STT. Azure OpenAI currently supports chat/image work only; audio transcription through Azure is not wired yet.

Secrets themselves should stay out of JSON config. The config can store secret environment variable names instead, so agents or humans can choose which variables Zakira.Replay reads without embedding keys on disk. For example, `llm.openai.apiKeyEnvVars=OPENAI_API_KEY,WORK_OPENAI_API_KEY` tells Zakira.Replay to try those variable names for the OpenAI API key. Built-in defaults are still appended, so standard names keep working.

## Batch Manifest

```json
{
  "instruction": "Extract transcript and frames for later summarization.",
  "frames": 7,
  "useSpeechToText": true,
  "useOcr": true,
  "useVision": true,
  "useSummary": true,
  "items": [
    { "source": "https://example.com/video1", "runId": "video-1" },
    { "source": "C:/media/video2.mp4", "frames": 5 }
  ]
}
```

The batch runner calls the same single-video pipeline for each item and writes a batch summary under `runs/`.

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

Frame extraction defaults to interval sampling. Use `--frame-strategy scene` to ask `ffmpeg` for scene-change frames; if no scene frames are found, Zakira.Replay falls back to interval sampling. Use `--frame-strategy every-frame` or `--every-frame` for capped sequential frame extraction, where `--frames`/`--count` is the safety cap.

Clip extraction writes timestamped clips under `clips/`:

```bash
zakira-replay clip C:\media\demo.mp4 --start 01:20 --end 02:05 --output-name dashboard-demo
```

Search indexing builds over `evidence.json` transcript, OCR, vision, summary, and warnings. The default backend is a portable JSON TF-IDF index at `search/index.json`:

```bash
zakira-replay search build runs\example-run
zakira-replay search query runs\example-run "wireguard throughput" --top 5
```

SQLite search is also available. `sqlite` builds `search/index.sqlite` with FTS5 keyword/BM25 search. `sqlite-onnx` additionally stores local ONNX embedding vectors as float32 blobs and queries with hybrid FTS plus brute-force cosine scoring:

```bash
zakira-replay search build runs\example-run --backend sqlite
zakira-replay search build runs\example-run --backend sqlite-onnx --onnx-model C:\models\embedding.onnx --onnx-vocab C:\models\vocab.txt
zakira-replay search query runs\example-run "secure tunnel performance" --backend auto --top 5
```

ONNX embedding support expects a BERT/WordPiece-style `vocab.txt` plus an ONNX model with common text inputs such as `input_ids`, `attention_mask`, and optional `token_type_ids`. Zakira.Replay does not bundle a model.

Download a compatible local ONNX embedding model with either command:

```powershell
zakira-replay deps install onnx
.\scripts\download-onnx-model.ps1 -Configure
```

Both download `Xenova/all-MiniLM-L6-v2` files. The built-in installer uses the configured `search.onnx.modelDirectory`; the script downloads under repository-local `models/`, which is ignored by git.

Chapter detection builds deterministic offline lexical chapters from transcript topic shifts and duration constraints:

```bash
zakira-replay chapters build runs\example-run --min-duration 60 --max-duration 600
```

It writes `chapters/chapters.json` and `chapters/chapters.md`.

## MCP Jobs

MCP exposes both a blocking compatibility tool and non-blocking job tools:

- `analyze_video`: starts analysis and waits for completion. Use only for short videos.
- `create_analysis_job`: starts analysis in the background and returns a `jobId`.
- `get_job_status`: returns status and recent logs.
- `get_job_result`: returns the completed manifest and artifact directory.
- `cancel_job`: cancels a running job.
- `extract_clip`: extracts a timestamped video clip.
- `build_search_index`: builds a local search index over a completed run. Optional `backend` values are `json`, `sqlite`, and `sqlite-onnx`.
- `query_search_index`: queries a run directory or search index. Optional `backend` values are `auto`, `json`, `sqlite`, and `sqlite-onnx`.
- `build_chapters`: builds transcript-based chapters for a completed run and writes `chapters/chapters.json` plus `chapters/chapters.md`.

Agents should prefer the job tools for long videos or LLM-backed OCR/vision/summary work.

MCP job snapshots are persisted under `runs/.mcp/jobs/`. Completed job status and results survive MCP server restarts. Jobs that were pending or running when the server stopped are restored as failed with a restart message.

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

Zakira.Replay does not generate books, reports, presentations, or PDFs. It provides video evidence that external orchestrators can consume.

Each analyzed video run writes a folder under `runs/` containing:

- `request.json`: original source, instruction, transcript flag, frame count, and optional run ID.
- `metadata.json`: source metadata resolved from the URL or local file.
- `manifest.json`: stable summary of produced artifacts.
- `evidence.json`: structured evidence for downstream agents/orchestrators.
- `transcript.md`: normalized timestamped transcript when captions or sidecar subtitles are available.
- `transcript/raw.md` and `transcript/raw.json`: raw parsed transcript before normalization.
- `transcript/normalization.json`: transcript merge audit report with merge reasons and source/result segments.
- `captions/`: raw extracted subtitle files.
- `audio/`: extracted audio when requested or needed for STT.
- `frames/`: representative frame images.
- `ocr/`: frame OCR results when requested.
- `vision/`: frame visual descriptions when requested.
- `summary.md`: evidence summary when requested.
- `evidence.md`: human-readable summary of the artifact paths.

JSON schemas for stable machine-readable artifacts are in `schemas/`:

- `schemas/request.schema.json`
- `schemas/manifest.schema.json`
- `schemas/evidence.schema.json`
- `schemas/transcript-normalization.schema.json`
- `schemas/chapters.schema.json`
- `schemas/clip.schema.json`
- `schemas/search-index.schema.json`
- `schemas/batch.schema.json`
- `schemas/batch-result.schema.json`
- `schemas/queue.schema.json`
- `schemas/queue-run-result.schema.json`

External orchestration can use these artifacts to build conference books, summaries, search indexes, vector stores, QA systems, clip workflows, or custom reports.

## Development

Run the test suite with:

```bash
dotnet test Zakira.Replay.slnx
```

The ffmpeg integration test generates a tiny fixture at runtime and skips automatically when `ffmpeg` or `ffprobe` is unavailable.

## License

Zakira.Replay is released under the [MIT License](LICENSE).
