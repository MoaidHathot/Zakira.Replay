# Changelog

All notable changes to Zakira.Replay are documented in this file. Format is loosely based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Each release lists user-visible changes plus the underlying contract changes (schemas,
warning codes, env vars, config keys) so orchestrators can plan migrations.

## [0.9.0] — MCP + CLI modernization (breaking)

This release is a deliberate hard break of the MCP and CLI surfaces. The hand-rolled
JSON-RPC server and command parser shipped through 0.8.x have been retired in favour of
the official `ModelContextProtocol` C# SDK (1.3.0) and `System.CommandLine` 3.0-preview.
Every existing agent and script that talks to Zakira.Replay needs to update to the new
names.

### Breaking — MCP tool renames (`verb.noun`)

| 0.8.x tool name                  | 0.9.0 tool name      |
| -------------------------------- | -------------------- |
| `analyze_video`                  | `analyze`            |
| `create_analysis_job`            | `analyze.start`      |
| `get_job_status`                 | `analyze.status`     |
| `get_job_result`                 | `analyze.result`     |
| `cancel_job`                     | `analyze.cancel`     |
| `enqueue_analysis_queue_job`     | `queue.enqueue`      |
| `run_analysis_queue`             | `queue.run`          |
| `get_analysis_queue_status`      | `queue.status`       |
| `extract_clip`                   | `clip`               |
| `extract_frames`                 | `frames`             |
| `build_search_index`             | `index.build`        |
| `query_search_index`             | `index.query`        |
| `build_chapters`                 | `chapters.build`     |
| `build_evidence_alignment`       | `align`              |
| `discover_videos`                | `discover`           |
| `doctor`                         | `doctor`             |

### Breaking — CLI verb renames (`noun verb`)

| 0.8.x command           | 0.9.0 command           |
| ----------------------- | ----------------------- |
| `search build <dir>`    | `index build <dir>`     |
| `search query <dir> q`  | `index query <dir> q`   |
| `align <dir>`           | `align build <dir>`     |
| `deps path`             | `deps status`           |
| `llm ask <prompt>`      | `llm chat <prompt>`     |
| `doctor --json`         | `doctor --output-format json` |
| `info --json`           | `info --output-format json`   |
| `queue status --json`   | `queue status --output-format json` |

### Added — MCP

- **Official `ModelContextProtocol` SDK** powers `zakira-replay mcp serve`. Tool input
  schemas are auto-generated from C# method signatures — no more hand-maintained JSON
  schemas inside the server.
- **Resources** (`replay://`) — agent-readable artifacts at stable URIs without firing a
  tool call:
  - `replay://runs` (run index)
  - `replay://runs/{id}/manifest` / `evidence` / `transcript` / `chapters`
  - `replay://runs/{id}/aligned/by-chapter` / `aligned/by-slide`
  - `replay://runs/{id}/frames/{frameId}/ocr` / `vision`
  - `replay://jobs/{jobId}/logs` (live job log buffer)
- **HTTP + SSE transports** — `zakira-replay mcp serve --transport http --port 8765`
  hosts a Streamable HTTP MCP endpoint (Stateless) so hosted-agent platforms can talk to
  Zakira.Replay without spawning a stdio subprocess. `--transport sse` is an alias for the
  same Streamable HTTP endpoint (the SSE transport was folded into Streamable HTTP in the
  MCP spec). `--transport stdio` remains the default for Claude Desktop / Cursor / VS Code
  Copilot.

### Added — CLI

- **System.CommandLine 3.0** parser. Brings auto-generated `--help`, shell completion
  (`zakira-replay completion {bash|zsh|pwsh|fish}`), validated enum options, and
  `Ctrl-C → CancellationToken` plumbing for free.
- **`runs` group** (new) — first-class run inspection without grepping the filesystem:
  - `runs list` (most-recent-first)
  - `runs show <id>` (path summary plus manifest in JSON when `--output-format json`)
  - `runs delete <id> --force`
  - `runs export <id> --format md|jsonl`
- **`--preset meeting|lecture|demo|interview|raw`** on `analyze` — opinionated defaults
  bundles so agents don't have to pass ten flags for the common scenarios. `meeting`
  turns on `--ocr --vision --diarize --stt`; `lecture` enables `--ocr --vision`; `demo`
  prefers the scene-cut frame strategy; `interview` skips frames and runs diarization.
  Explicit flags always win.
- **Global flags** (recursive across every subcommand):
  - `--output-format text|json|ndjson` (replaces ad-hoc `--json`)
  - `--log-file <path>`
  - `--log-level info|debug|trace`
  - `--correlation-id <string>` (intended for cross-tool tracing)

### Added — reliability

- **`runs.directory` config key + `ZAKIRA_REPLAY_RUNS_DIRECTORY` env var** — pins the
  output folder for every `runs/<run-id>/` artifact tree instead of inheriting whatever
  the current working directory happened to be. Stored env-var literals are preserved
  (`%LOCALAPPDATA%\Zakira.Replay\runs` stays literal in the JSON; the value is expanded
  at read time) so a single config file is portable across machines. Resolution
  precedence: env var → config → legacy `<cwd>/runs`. `info` reports the resolved
  absolute path; `config get runs.directory` returns the stored literal. Same shape as
  the existing `dependencies.portableDirectory` knob.
- **Atomic artifact writes** — `ArtifactStore.WriteJsonAsync` and `WriteTextAsync` now
  write to a sibling `.tmp` file and rename it over the destination. Same-volume rename
  is atomic on every supported filesystem, so a Ctrl-C / crash mid-serialization can no
  longer leave a corrupt `manifest.json` or `evidence.json` for downstream agents.
- **Ctrl-C handler** — `Program.cs` now wires `Console.CancelKeyPress` to a real
  `CancellationTokenSource` instead of passing `CancellationToken.None`. Long-running
  pipelines and subprocesses (yt-dlp, ffmpeg, Playwright) get a chance to clean up.

### Added — services

- **`Zakira.Replay.Core.ServiceCollectionExtensions.AddReplay`** — registers the full
  service graph (`ConfigStore`, `ReplayConfig`, `DependencyResolver`, `ArtifactStore`,
  `IYtDlpClient`, `IFfmpegClient`, `IBrowserVideoCaptureClient`, `AnalysisPipeline`,
  `ClipExtractionService`, `FrameCaptureService`, `DiscoveryService`,
  `SearchIndexService`, `ChapterBuilder`, `EvidenceAlignmentService`, plus
  `Func<AnalysisPipeline>` for queue/job factories) with
  `Microsoft.Extensions.DependencyInjection`. The MCP host and the CLI's MCP tests both
  consume this extension; downstream embedders can host Zakira.Replay without rebuilding
  the dependency graph by hand.

### New packages

- `Microsoft.Extensions.Hosting` 10.0.8
- `Microsoft.Extensions.DependencyInjection` 10.0.8
- `Microsoft.Extensions.Logging.Console` 10.0.8
- `ModelContextProtocol` 1.3.0
- `ModelContextProtocol.AspNetCore` 1.3.0 (for the HTTP/SSE MCP transport)
- `System.CommandLine` 3.0.0-preview.4

### Removed

- `Zakira.Replay.Mcp.McpServer` (the hand-rolled JSON-RPC dispatcher) — replaced by
  `ReplayTools` + `ReplayResources` + `McpHost` on top of the official SDK.
- `Zakira.Replay.Cli.CommandOptions` (the hand-rolled argv parser) — replaced by
  System.CommandLine's `Option<T>` / `Argument<T>` bindings.
- `--json` flag on individual commands — superseded by the global `--output-format json`.

### Migration notes

Agents currently calling 0.8.x tool names: update the tool name to the 0.9.0 equivalent
from the table above. Schemas for individual tool arguments stayed conceptually identical
(same property names and same types) but `ListToolsAsync()` will only return the new
names — there is no deprecation shim.

CLI scripts: update `search build` → `index build`, `search query` → `index query`,
`align` → `align build`, `deps path` → `deps status`, `llm ask` → `llm chat`, and replace
per-command `--json` with the global `--output-format json`.

MCP transport upgrades: the new HTTP transport requires `ModelContextProtocol.AspNetCore`,
which is already a direct dependency of the global tool nupkg. No extra install needed.

## [Unreleased] — Dedicated Edge profile for browser capture

### Added
- **`zakira-replay auth init-edge-profile`** — one-command per-machine setup of a dedicated
  Microsoft Edge user-data-dir for browser-capture mode. Launches the real Edge binary (no
  Playwright automation surface) with `--user-data-dir <configured-dir>`, waits for the user
  to sign in interactively, and verifies a Cookies file appeared after Edge exits. Cookies
  are stored in Edge's native DPAPI-encrypted SQLite (per-user, per-machine on Windows;
  Keychain on macOS; libsecret/KWallet on Linux) so the on-disk auth state is not a portable
  bearer token like the `StorageState` JSON produced by `auth login`.
- **`capture.browser.edgeUserDataDir`** config key — absolute path to the dedicated Edge
  user-data-dir. Stored verbatim (env-var literals like `%LOCALAPPDATA%` preserved) so the
  config travels across machines; expansion happens at read time. Default
  `%LOCALAPPDATA%\Zakira.Replay\edge-profile` (resolved per-machine via
  `Environment.SpecialFolder.LocalApplicationData`).
- **`capture.browser.edgeProfileDirectory`** config key — Chromium `--profile-directory` value
  (sub-folder inside `EdgeUserDataDir`). Defaults to `"Default"`.
- **Persistent-context capture path** in `PlaywrightVideoCaptureClient` — when the configured
  Edge user-data-dir contains a Cookies file under the named sub-folder, capture switches
  from `LaunchAsync` + `NewContextAsync(StorageStatePath)` to
  `LaunchPersistentContextAsync(userDataDir, ...)`. Activates implicitly; no CLI flag needed.
- **Post-navigation auth-failure detection** — capture inspects the final URL against
  canonical Microsoft / OAuth / SAML sign-in domains and probes for Entra ID MFA challenge
  selectors before duration probing. Replaces the misleading `CAPTURE_DURATION_UNRESOLVED`
  timeout that previously hid expired sessions.
- **Browser-captured media for STT fallback** — when `--stt` is requested AND no inline
  captions were intercepted AND no audio was otherwise resolved, browser capture observes
  media-shaped responses (`video/*`, `audio/*`, HLS / DASH manifests) during playback, picks
  the largest single-file candidate, and re-downloads it via the authenticated Playwright
  context. The downloaded file lands at `media/browser-fetched.<ext>`; ffmpeg extracts audio
  and Whisper STT runs against it. Side-channel is off by default — only activates when STT
  needs audio and no other source is available, so routine browser-capture runs pay zero
  bandwidth cost. Works for single-file MP4 patterns (typical SharePoint Stream upload);
  does NOT handle HLS / DASH chunked streams (audio split across fragments) or DRM-protected
  streams — those emit `CAPTURE_BROWSER_MEDIA_NO_CANDIDATE` and STT is skipped with a clear
  reason. URL filter actively skips DASH / fMP4 fragment patterns (`part=mediasegment`,
  `segmentTime=`, `.m4s`) so we don't waste an authenticated download on a single segment
  ffmpeg can't decode.
- **Caption-track activation** — after `video.play()`, browser capture now walks
  `videoElement.textTracks` and sets each captions/subtitles track to `mode = "showing"`,
  forcing the player to fetch its cue source. Most players (SharePoint Stream, Microsoft
  Stream, YouTube embedded, generic HTML5) advertise tracks in metadata but only load the
  underlying `.vtt`/`.srt` when CC is toggled \u2014 without this nudge, the existing
  `CaptionResponseCollector` would never see any caption responses. Fallback heuristic also
  tries clicking common CC-labelled buttons (`button[aria-label*="caption" i]` etc.) when
  the textTracks API path doesn't activate anything. Emits
  `CAPTURE_BROWSER_CAPTIONS_ACTIVATED` (info) when one or more tracks were activated, so
  orchestrators can see the activation path was used.
- **Direct cue harvest from `textTracks`** — for players that build caption cues client-side
  via `track.addCue()` rather than fetching a `.vtt` (no network response for the existing
  interceptor to catch), browser capture now reads cues directly out of the
  `videoElement.textTracks[i].cues` arrays after activation. Serialises them to standard
  WebVTT and saves under `captions/texttrack-NNNN-<lang>.vtt`. The existing transcript-fill
  logic picks them up unchanged. Emits `CAPTURE_BROWSER_CAPTIONS_HARVESTED_FROM_DOM` (info)
  with cue and track counts.
- **Diagnostic capture (`--capture-debug`)** \u2014 opt-in flag (also `capture.browser.debug=true`
  in config) that, during the existing browser-capture session, writes a diagnostic dump
  under `runs/<id>/debug/`: `network.log` (JSONL of every response with URL, status,
  content-type, size, headers, timestamp), `metadata-responses/<seq>-<sha8>.<ext>` (full
  bodies for JSON / XML / text / JavaScript responses under `capture.browser.debugMaxBodyBytes`,
  default 1 MB), `metadata-responses/index.json` (URL \u2192 body file mapping with SHA-256s),
  `texttracks-state.json` (snapshot of `<video>.textTracks` post-activation), and
  `network.har` (standard HAR file via Playwright's `RecordHarPath`). Designed for
  reverse-engineering vendor-specific players (SharePoint Stream, Vimeo, Wistia, internal
  portals) without affecting capture behaviour \u2014 strictly side-channel.
- **`scripts/probe-graph-availability.ps1`** \u2014 standalone PowerShell 7+ script that probes
  whether your Entra ID tenant accepts the Microsoft Graph PowerShell SDK's public client ID
  (`14d82eec-204b-4c2f-b7e8-296a70dab67e`). Optionally resolves a Stream URL to a drive
  item and tests the transcripts endpoint. Writes a structured JSON report and a one-line
  summary. Pre-flight for any future Graph fast-path integration; runs read-only against
  the tenant.
- **SharePoint Stream / Microsoft Stream native transcript support** \u2014 new
  `SharePointStreamInterceptor` recognises the Stream player's transcripts-metadata API
  call (`_api/v2.X/drives/{drive-id}/items/{item-id}?...media/transcripts`), parses the
  JSON for `media.transcripts[]`, and follows each entry's `temporaryDownloadUrl` via the
  authenticated Playwright context (Edge profile cookies) to download the transcript file.
  Tries multiple URL variants in priority order to coax out the richest format: first the
  `?isformatjson=true&transcriptkey=<id>` query the Stream player itself uses (which
  returns the full Microsoft Teams transcript JSON, `$schema:transcript.json`, with
  `speakerDisplayName`, `speakerId`, `confidence`, `roomId`, ISO 8601 `startOffset` /
  `endOffset` per entry), falling back through `$format=json`, `format=json`, and finally
  the plain URL (which returns a stripped public WebVTT without speakers). When the rich
  JSON is obtained, the converter emits proper VTT `<v Speaker>` voice spans so the
  existing `SubtitleConverter` picks up speaker attribution. Wired as the third
  caption-source layer after network-`.vtt` interception and textTracks-cue harvest;
  activates automatically for Stream URLs with zero configuration. Also includes a
  proactive metadata-fetch fallback: when the player happens not to query the
  transcripts-metadata endpoint during automation (observed varying by recording),
  Zakira queries it itself using the `(drive-id, item-id)` harvested from any other
  SharePoint REST call (`labelPolicies`, `analytics/allTime`, etc.) observed on the same
  item. Raw response bodies are persisted alongside the converted VTT under
  `captions/stream-NNNN-<lang>.{vtt,json}` for audit.
- **`edge-profile` synthetic dependency** in `doctor` output — reports `ready` / `not
  initialized` / `locked` / `missing` for the configured Edge user-data-dir, mirroring the
  existing `whisper-model`, `ocr-models`, `vision-models` entries.
- **`ZAKIRA_REPLAY_EDGE_USER_DATA_DIR`** env var override for the Edge profile path, mirroring
  the existing `ZAKIRA_REPLAY_AUTH_DIRECTORY` pattern.

### Warning codes added
- `CAPTURE_BROWSER_PROFILE_NOT_INITIALIZED` (info) — Edge profile dir has no Cookies file
  yet. Capture falls back to the StorageState path; run `auth init-edge-profile` to upgrade.
- `CAPTURE_BROWSER_PROFILE_DIR_MISSING` (error) — explicit `edgeUserDataDir` points at a
  non-existent directory; capture aborts.
- `CAPTURE_BROWSER_PROFILE_LOCKED` (error) — `SingletonLock` present inside the profile
  sub-folder; another Edge process is using the dir. Close Edge and retry.
- `CAPTURE_BROWSER_PROFILE_LAUNCH_FAILED` (error) — `LaunchPersistentContextAsync` threw
  (corrupt profile, DPAPI unavailable, incompatible Edge version). Playwright message
  included.
- `CAPTURE_BROWSER_AUTH_REQUIRED` (error) — post-navigation URL matched a sign-in domain;
  the browser context is not signed in. Re-run `auth init-edge-profile --url <site>`.
- `CAPTURE_BROWSER_AUTH_MFA_DETECTED` (error) — page contains a Microsoft MFA challenge
  selector that headless capture cannot satisfy. Re-init interactively.
- `CAPTURE_PROFILE_CONFLICT` (info) — both `--auth-profile` and an initialized
  `edgeUserDataDir` were supplied; persistent-context wins.
- `CAPTURE_BROWSER_MEDIA_DOWNLOADED` (info) — STT-fallback media download succeeded.
- `CAPTURE_BROWSER_MEDIA_NO_CANDIDATE` (info) — no single-file media URL observed (typically
  chunked stream); STT skipped.
- `CAPTURE_BROWSER_MEDIA_DOWNLOAD_FAILED` (warning) — authenticated media re-download
  failed; STT skipped.
- `CAPTURE_STREAM_TRANSCRIPT_DISCOVERED` (info) \u2014 Stream transcripts-metadata endpoint
  observed and parsed; lists language + source for each transcript found.
- `CAPTURE_STREAM_TRANSCRIPT_DOWNLOADED` (info) \u2014 per-transcript download via authenticated
  context succeeded; reports language, byte count, output path.
- `CAPTURE_STREAM_METADATA_PARSE_FAILED` (warning) \u2014 the transcripts-metadata response body
  wasn't valid JSON or lacked the expected `media.transcripts[]` array.
- `CAPTURE_STREAM_TRANSCRIPT_PARSE_FAILED` (warning) \u2014 transcript body downloaded but could
  not be converted to WebVTT (unknown format); raw body kept for inspection.

### Changed
- `BrowserCaptureRequest` record gained four optional fields (`EdgeUserDataDir`,
  `EdgeProfileDirectory`, `CaptureMediaForStt`, `MaxMediaBytes`). Additive; existing call
  sites compile unchanged.
- `BrowserCaptureResult` record gained `DownloadedMediaPath` (optional). Additive.
- `auth` subcommand usage hint extended to list `init-edge-profile`.

### Migration notes
- Existing users who never set `capture.browser.edgeUserDataDir` see one new info-level
  warning per browser-capture run: `CAPTURE_BROWSER_PROFILE_NOT_INITIALIZED`. Behaviour is
  otherwise unchanged — capture falls back to the StorageState path. Run
  `zakira-replay auth init-edge-profile` once to silence the warning and start using
  DPAPI-encrypted cookies.
- The config file remains backward-compatible; the two new keys default to null. Existing
  StorageState auth profiles continue to work via the `--auth-profile` flag.

## [Unreleased] — Florence-2 local image captioner (replaces BLIP)

### Added
- **`--local-vision-mode clip-caption`** ships with a working Florence-2-base-ft image
  captioner. Auto-downloaded via `zakira-replay deps install vision --mode clip-caption`
  (~410 MB total: CLIP ~150 MB + Florence ~260 MB; tracks `onnx-community/Florence-2-base-ft`
  main branch on Hugging Face). Caption text fills `VisionFrameStructured.FreeText` with the
  pattern `"Frame appears to show: <model caption>. Visible text: <OCR concat>"` so the
  trustworthy OCR text is always available alongside the model-derived description.
- **`FlorenceBartBpeTokenizer`** (`Core/FlorenceBartBpeTokenizer.cs`) - hand-rolled BART-style
  byte-level BPE tokenizer with the GPT-2 `bytes_to_unicode` table. Loads vocab.json +
  merges.txt + added_tokens.json from the canonical onnx-community export. ~290 LOC. No new
  NuGet dependency.
- **4-graph Florence-2 orchestration** in `LocalOnnxVisionProvider.GenerateFlorenceCaptionAsync`:
  vision_encoder → embed_tokens → encoder_model → decoder_model_merged, greedy decoding,
  EOS detection. Image preprocessing at 768x768 with ImageNet mean/std per Florence's
  preprocessor_config.json. Task prompt hard-coded to `<DETAILED_CAPTION>` expanded to
  "Describe in detail what is shown in the image." per upstream documentation.
- **New config knobs**: `vision.local.florence{VisionEncoder,Encoder,Decoder,EmbedTokens,Vocab,Merges,AddedTokens}Path`, `vision.local.florenceMaxTokens` (default 80), `vision.local.florenceQuantization` (default `quantized` / int8; also accepts `fp16`, `q4`, `q4f16`, `bnb4`, `int8`, `uint8`, `full`). Mirroring env vars `ZAKIRA_REPLAY_VISION_FLORENCE_*`.

### Changed
- **`LocalVisionMode.ClipBlip` renamed to `LocalVisionMode.ClipCaption`.** Same integer value
  (`2`) is preserved for ABI compat. The string forms `"clip-blip"` / `"clip+blip"` / `"blip"`
  are still accepted by `VisionProviderFactory.NormalizeMode` as deprecated aliases mapping
  to the new value. Canonical string form is now `"clip-caption"`.
- BLIP-specific config keys (`vision.local.blip*Path`, `vision.local.blipMaxTokens`) and
  `LocalVisionOptions.Blip*` fields are **removed**. The previous BLIP integration was never
  functional (no auto-download flow shipped; no public ONNX export was validated). Florence-2
  replaces it.
- `LocalVisionOptions` record signature changed: new `Quantization` parameter, new
  `Florence*` paths, removed `Blip*` paths and `BlipMaxTokens` field.
- `PortableDependencyInstaller.InstallVisionModelsAsync` now downloads Florence-2 ONNX from
  `onnx-community/Florence-2-base-ft` for `--mode clip-caption`. Files saved under canonical
  names (`florence-vision-encoder.onnx`, `florence-encoder.onnx`, `florence-decoder.onnx`,
  `florence-embed-tokens.onnx`, `florence-vocab.json`, `florence-merges.txt`,
  `florence-added-tokens.json`) regardless of source filename or quantization variant.

### Demo D results (live verification)
- Source: `https://www.youtube.com/watch?v=Ws-Nc9S8i_Y` (ThePrimeagen video about npm
  Shai-Hulud supply-chain attack).
- 50 frames analyzed end-to-end with **zero LLM calls**.
- Kind distribution: 30 code, 13 ui, 7 slide, 0 other (identical to Demo C since CLIP
  classifier didn't change).
- Caption examples (Florence-2-base-ft, int8 quantized):
  - frame-001 [00:04]: "In this image we can see a person wearing black color T-shirt is
    standing and speaking in the microphone. In the background, we can see the screen with
    some text and images."
  - frame-012 [00:59]: "In this image we can see a person holding a mic. In the background of
    the image there is some text." (Visible text: ChainPatrol @ChainPatrol Mni Shai-Hulud
    (CVE-2026-45321) is a supply chain wori...)
  - frame-056 [04:38]: "In this image we can see a screen. On the screen we can see a person
    wearing headphones and holding a mic. Also something is written on the screen."
- All 50 results tagged `"provider": "local"`. Run on disk: `runs/demo-vision-d-florence/`.

### Honest caveats
- **KV-cache decoding is enabled** (`decoder_model_merged.onnx` with `use_cache_branch=true`
  after step 0; encoder cross-attention K/V captured once and reused; decoder self-attention
  K/V threaded forward). Measured speedup on the int8 quantized models is modest — ~12% on
  a 3-frame benchmark (2.76 s/frame uncached → 2.42 s/frame cached) — because the
  autoregressive decoder loop is only ~12% of per-frame wall-clock time. The dominant cost
  is the vision encoder + image preprocessing subprocess, neither of which KV-cache touches.
  Captions also drift slightly between the cached and uncached paths (different word
  choices, same scene) because int8 op fusion differs between the `use_cache_branch=true/false`
  subtrees of the merged graph; greedy decoding amplifies that into different tokens. Both
  captions are coherent and accurate.
- **Florence-2-base captions are smaller-model captions.** Useful and grammatical but less
  detailed than a frontier vision LLM. Always paired with literal OCR text in `freeText` so
  the trustworthy part is preserved.
- **`charts[]` still always empty** in local mode (unchanged from prior release).

## [Unreleased] — Local vision provider (no LLM)

### Added
- **`--vision-provider local` / `visionProvider: "local"`.** New fully-on-device vision path
  via `LocalOnnxVisionProvider`. Never invokes an LLM. Three selectable sub-modes via
  `--local-vision-mode` (CLI) / `localVisionMode` (MCP) / `vision.local.mode` (config):
  - `heuristic` (zero models) - structure derived entirely from the per-frame OCR result via
    `OcrToVisionHeuristics`. Kind by score-based token classification, Title by top-line
    detection, Bullets by glyph + numbering patterns, CodeBlocks by symbol density +
    indentation runs, UiElements by curated whitelist + layout heuristics.
  - `clip` - heuristic + CLIP ViT-B/32 zero-shot classification fills the `kind` field. Needs
    a CLIP image-encoder ONNX and a pre-computed kind-embeddings binary (~150 MB total).
  - `clip-blip` (default for the local provider) - above plus BLIP-base image captioning
    fills `freeText` with a model-derived visual description prefixed by
    `"Frame appears to show: ..."` so consumers know it came from a smaller captioner. Needs
    BLIP image encoder + decoder + WordPiece vocab (~400 MB additional).
- **`IVisionProvider` interface refactored** to take a `VisionRequest(ImagePath, Instruction, Frame, OcrContext?)` record so the local provider can consume per-frame OCR results. Single internal callsite updated; `CopilotVisionProvider` continues to ignore `OcrContext`.
- **OCR auto-enable.** When `--vision-provider local` is passed without `--ocr`, the pipeline
  silently enables OCR for the run and records `VISION_LOCAL_OCR_REQUIRED` (info) so the
  orchestrator can see what happened. Pass `--ocr` explicitly to silence the warning.
- **Graceful mode degradation.** If `clip-blip` is requested but BLIP models are missing the
  provider downgrades to `clip`. If CLIP models are also missing it downgrades to
  `heuristic`. Each step emits `VISION_LOCAL_MODE_DEGRADED` (warning) listing the missing
  files so the orchestrator can wire up `vision.local.*` config keys or run
  `deps install vision`.
- **`VisionProviderFactory`** mirroring `OcrProviderFactory`: `GetConfiguredProvider`,
  `Normalize`, `GetConfiguredLocalMode`, `NormalizeMode`, `FormatMode`. Stable
  `VisionProviders.{Copilot,Local}` constants and `LocalVisionMode` enum.
- **`LocalVisionOptions`** with `Resolve(ReplayConfig?)` (mirrors `LocalWhisperOptions` and
  `LocalOcrModelPaths`): resolves env vars → config → portable-dir defaults. Helper methods
  `RequiredFilesFor`/`MissingFilesFor` make missing-model diagnostics precise.
- **`IFfmpegClient.PreprocessImageRgb24Async`** - shared image preprocessing primitive (resize
  + RGB24 raw output) used by CLIP/BLIP modes. Reuses the existing perceptual-hash pattern;
  no new image library dependency.
- **New `vision.*` config keys**: `vision.provider`, `vision.local.mode`,
  `vision.local.modelDirectory`, `vision.local.clipImageEncoderPath`,
  `vision.local.clipTextEncoderPath`, `vision.local.clipKindEmbeddingsPath`,
  `vision.local.blipImageEncoderPath`, `vision.local.blipDecoderPath`,
  `vision.local.blipVocabPath`, `vision.local.blipMaxTokens`, `vision.local.autoDownload`.
- **New env vars**: `ZAKIRA_REPLAY_VISION_PROVIDER`, `ZAKIRA_REPLAY_VISION_LOCAL_MODE`,
  `ZAKIRA_REPLAY_VISION_CLIP_IMAGE_ENCODER_PATH`,
  `ZAKIRA_REPLAY_VISION_CLIP_KIND_EMBEDDINGS_PATH`,
  `ZAKIRA_REPLAY_VISION_BLIP_*`.
- **New warning codes**: `VISION_LOCAL_MODELS_MISSING`, `VISION_LOCAL_INIT_FAILED`,
  `VISION_LOCAL_INFERENCE_FAILED`, `VISION_UNKNOWN_PROVIDER`, `VISION_LOCAL_OCR_REQUIRED`,
  `VISION_LOCAL_MODE_DEGRADED`.

### Changed
- `VisionFrameResult` gains optional `Provider` field (mirrors `OcrFrameResult.Provider`).
  `"copilot"` for the existing LLM path; `"local"` for the new provider. Older runs that
  omit the field still validate against the schemas.
- `AnalysisCache` key tuple extended with `VisionProvider` + `LocalVisionMode` so flipping
  the provider or sub-mode correctly invalidates prior cached runs.
- `AnalyzeRequest` gains `VisionProvider` (default `copilot`) and `LocalVisionMode`
  (nullable) fields. Existing requests deserialise unchanged.
- README: new "Watching videos without an LLM" / "Local Vision" section covering the three
  modes, the model-bring-your-own flow, and the honest limitations table (Charts always
  empty in local mode, etc.).

### Schema
- `evidence.schema.json`, `vision.schema.json`: optional `provider` field on
  `visionFrameResult`. Additive, no schema-version bump.
- `request.schema.json`: new optional `visionProvider` (enum) and `localVisionMode` (enum).
- `queue.schema.json`: same two fields on the embedded `analyzeRequest`.

### Notes for orchestrators
- `clip` mode now installs end-to-end via `zakira-replay deps install vision --mode clip` (downloads CLIP ViT-B/32 ONNX from `Xenova/clip-vit-base-patch32` on Hugging Face, ~150 MB) followed by `zakira-replay vision generate-clip-embeddings` (writes the 14336-byte `clip-kind-embeddings.bin`). `clip-blip` still requires user-supplied BLIP ONNX files (auto-download deferred to a future release pending a validated upstream export); see README "Bringing your own BLIP".
- Vision quality with the local provider is structurally weaker than the LLM path on
  charts (always empty), diagrams without labels, icon enumeration, and free-form scene
  description. Use it where text-on-screen extraction matters more than free-form scene
  understanding (slide decks, code walkthroughs, dashboard captures).
- **Demo C results vs heuristic baseline** (real run captured against the YouTube video
  `Ws-Nc9S8i_Y`, a ThePrimeagen code-review screencast about the Shai-Hulud npm supply-chain
  attack): heuristic mode classified 48/50 frames as `other` and 2/50 as `code`. Clip mode
  classified 30/50 as `code`, 13/50 as `ui`, 7/50 as `slide`, and 0/50 as `other` — every
  frame received a non-default kind. Some classifications were arguably imperfect (the
  ChainPatrol headline frame at 00:59 classified as `code` because of its dark theme + dense
  text, more accurately `slide`), but the upgrade over the heuristic-mode baseline is
  unambiguous.

### Tools
- New `zakira-replay vision generate-clip-embeddings [--text-encoder <path>] [--vocab <path>] [--merges <path>] [--out <path>]` subcommand. Hand-rolled CLIP BPE tokenizer (`Cli/VisionGenerateClipEmbeddingsCommand.cs`) + ONNX Runtime text-encoder inference + L2-normalised 512-d output × 7 kind prompts → 14336-byte binary. No new NuGet dependency.
- New `vision-models` row in `zakira-replay doctor` reporting which mode is ready.
- `zakira-replay deps path` now also lists the vision model directory and the five CLIP file paths.

## [Unreleased] — Ad-hoc frame capture

### Added
- **`extract_frames` MCP tool + `zakira-replay frames --at`/`--from`/`--to` CLI flags.** Agents
  that have already analyzed a video can now ask for stills at specific moments (a list of
  exact timestamps) or inside a fixed window (`--from`/`--to` with `--count` and
  `--strategy interval|scene`) without paying for the full analyze pipeline. Frames land in a
  new `runs/<id>/frames/` folder alongside a minimal `frame-capture.json` manifest (schema:
  `frame-capture.schema.json`, `kind: "frame-capture"`). Optional `--max-edge` and `--quality`
  knobs let agents request thumbnail-sized JPEGs; `--phash` opts into perceptual-hash
  enrichment per frame.
- **`FrameCaptureService`** (`Core/FrameCapture.cs`) - public service usable from in-proc
  callers. New `TimeRange`, `FrameCaptureRequest`, `FrameCaptureResult`, `FrameCaptureManifest`,
  `FrameCaptureRequestSummary` value types and `FrameCaptureInput.ParseTimestamps` helper.
- **`IFfmpegClient.ExtractFramesAtAsync`** and **`IFfmpegClient.ExtractSceneFramesInRangeAsync`**
  for low-level callers. Scene mode uses output-side `-ss`/`-to` so reported timestamps stay in
  the absolute source timeline. New `FrameCaptureOptions(MaxLongEdgePixels, JpegQuality)`
  primitive carries resize / quality controls into ffmpeg invocations.
- **New strategy constants** `FrameSelectionStrategies.Timestamps` and `Range` (used by
  `FrameCaptureService` for self-documenting manifests; not by `AnalysisPipeline`).
- **New warning codes** under `ReplayWarningCodes`: `FRAME_CAPTURE_MEDIA_URL_UNRESOLVED`,
  `FRAME_CAPTURE_TIMESTAMP_OUT_OF_RANGE`, `FRAME_CAPTURE_RANGE_OUT_OF_BOUNDS`,
  `FRAME_CAPTURE_TOO_MANY_TIMESTAMPS`, `FRAME_CAPTURE_NO_FRAMES`,
  `FRAME_CAPTURE_SCENE_CAP_REACHED`.

### Changed
- `zakira-replay frames` rewired: when `--at`/`--from`/`--to` is supplied, routes through the
  new `FrameCaptureService`. Without those flags it falls back to the legacy full-analyze
  frames-only path so existing scripts keep working.
- Internal refactor: the existing per-timestamp ffmpeg loop in `FfmpegClient` is now shared by
  `ExtractFramesAsync` (interval strategy) and `ExtractFramesAtAsync` via a private
  `CaptureSingleFrameAsync` helper.

### Schema
- New `schemas/frame-capture.schema.json` describes the `frame-capture.json` artifact
  (`schemaVersion: "0.1"`, `kind: "frame-capture"`). `info --json`'s schema inventory now lists
  it. The existing `manifest.schema.json` is untouched - this is an additive sibling artifact,
  not an extension of the analyze manifest.

## [0.7.0] — Production hardening

### Added
- **Per-stage timings in manifests.** `manifest.timings` is now an additive field on every
  run (schema-validated): `totalSeconds` plus a `stages` map of wall-clock seconds per stage
  (e.g. `probe`, `stt`, `diarization`, `frames`, `slides`, `ocr`, `vision`, `evidence`).
  Stage names are open — orchestrators must tolerate new keys. The set of canonical stage
  names is exposed via `RunTimingStages` for consumers that pattern-match.
- **Extended `info --json`.** New top-level objects `resolvedDependencies` (portable directory,
  OCR pack, Whisper model path, Ollama endpoint and models, diarization directory) and
  `capabilities` (booleans for `localOcrReady`, `localWhisperReady`, `diarizationReady`,
  `ytDlpAvailable`, `ffmpegAvailable`). Orchestrators can now call `info --json` once at
  start-up to pre-flight which optional features are wired up, without separately running
  `doctor`.
- **`CHANGELOG.md`** retroactive for 0.2.0 → 0.7.0 (this file).
- GitHub Actions CI workflow building + running unit tests on Windows / Linux / macOS for
  every push and PR.

### Changed
- `ArtifactManifest` record gains an optional `Timings : RunTimingsArtifact?` field. Existing
  manifests without the field continue to deserialise (default `null`); existing schema
  validators must accept the additive change (allowed by `additionalProperties: false` because
  the field is explicitly declared as optional).

### Schema
- `manifest.schema.json` adds the optional `timings` object. `schemaVersion` stays at `0.8`.

## [0.6.0] — IChatClient migration + OCR language packs

### Added
- **OCR language pack framework.** `zakira-replay deps install ocr --language <pack>` downloads
  the requested PP-OCRv5 recognition model + dictionary alongside the shared detection /
  classification models. Multiple packs coexist on disk; switch via `ocr.local.languagePack`
  or `ZAKIRA_REPLAY_OCR_LANGUAGE_PACK`. Supported packs: `latin` (default), `chinese`,
  `english`, `korean`, `cyrillic`, `arabic`, `devanagari`, `greek`, `telugu`, `tamil`. All URLs
  HEAD-verified against ModelScope before release.
- New `ocr-models` row in `doctor` reporting the configured pack and missing-file list.
- OpenRouter / Together / Groq / vLLM / llama.cpp-server documentation: any OpenAI-compatible
  endpoint works via `--llm-provider openai` + `OPENAI_BASE_URL=<endpoint>`. No dedicated
  provider needed.

### Changed
- **Internal LLM provider refactor.** `OpenAiLlmProvider` and `AzureOpenAiLlmProvider` now go
  through `Microsoft.Extensions.AI.IChatClient` internally (via `Microsoft.Extensions.AI.OpenAI`
  and `Azure.AI.OpenAI`). Public `ILlmProvider` surface — constructors, `Name`, `CompleteAsync`,
  env var resolution, config keys, CLI flags — is unchanged. Audio transcription stays on the
  dedicated `/audio/transcriptions` HTTP route (IChatClient does not model STT). Both
  providers now implement `IDisposable` to release the chat client's transport.
- `LlmProviderTests` rewritten from `HttpMessageHandler` mocking to `IChatClient` mocking via a
  `RecordingChatClient` test double.

### Config
- `ocr.local.languagePack` (default `latin`) added. Aliases for set/get: `ocr.local.language`,
  `ocr.local.lang`, `ocr.local.pack`.

### Env vars
- `ZAKIRA_REPLAY_OCR_LANGUAGE_PACK`

## [0.5.0] — Local speaker diarization

### Added
- **Local sherpa-onnx diarization.** `--diarize` runs pyannote-segmentation-3.0 +
  3D-Speaker embedding + agglomerative clustering on the audio after STT, labelling each
  transcript segment with a `SPEAKER_NN` cluster. `transcript.md` is rewritten in place
  with `[SPEAKER_NN]` prefixes; the existing `TranscriptNormalizer` picks the labels back up
  automatically and populates the `speakers[]` registry plus per-slide / per-chapter speaker
  rollups in `evidence-aligned/by-{slide,chapter}.json`. No schema bump — the fields were
  already reserved.
- `--num-speakers <n>` (when known) and `--diarize-threshold <0.0-1.0>` (clustering cutoff
  when speaker count unknown).
- `zakira-replay deps install diarization` downloads both ONNX models (~32 MB total).
- New `diarization-models` row in `doctor`.
- New env vars `ZAKIRA_REPLAY_DIARIZATION_*` and config section `[diarization]`.

### Warning codes
- `DIARIZATION_NO_AUDIO`, `DIARIZATION_NO_TRANSCRIPT`, `DIARIZATION_MODELS_MISSING`,
  `DIARIZATION_INIT_FAILED`, `DIARIZATION_FAILED`, `DIARIZATION_UNKNOWN_PROVIDER`.

### Schema
- `request.schema.json`, `queue.schema.json`, `batch.schema.json` add `useDiarization`,
  `numSpeakers`, `diarizationThreshold` (additive optional).
- `llmProvider` enum extended with `ollama` and `local-whisper` (caught up from prior phases).

## [0.4.0] — Ollama + `IChatClient` abstraction

### Added
- **`--llm-provider ollama`** for fully-local chat and vision through an Ollama daemon at
  `http://localhost:11434` (or `ZAKIRA_REPLAY_OLLAMA_ENDPOINT` / `OLLAMA_HOST`). Backed by
  OllamaSharp's native `Microsoft.Extensions.AI.IChatClient` — full streaming, tool calls,
  image attachments via Claude/llama3.2-vision/llava. Audio attachments rejected with a
  pointer to `local-whisper`.
- `AsChatClient()` extension on every `ILlmProvider`. Ollama returns the native client; other
  providers go through a thin shim that translates `ChatMessage` sequences into the existing
  `LlmRequest`.
- New `ollama` row in `doctor` (2-second `/api/tags` daemon probe).
- New env vars `ZAKIRA_REPLAY_OLLAMA_*` and config section `[llm.ollama]`.

### Changed
- `Microsoft.Extensions.AI.Abstractions` is now a public dependency (was previously a private
  detail of OllamaSharp).

## [0.3.0] — Local Whisper STT

### Added
- **`--llm-provider local-whisper`** for fully-local speech-to-text via Whisper.net
  (whisper.cpp bindings). Closes the only mandatory-cloud step in the pipeline.
- `zakira-replay deps install whisper-model [--whisper-model tiny|base|small|medium|large-v3|large-v3-turbo]`
  (default `small`). Honours `HF_TOKEN` for Hugging Face rate-limit relief.
- New `whisper-model` row in `doctor`.
- New env vars `ZAKIRA_REPLAY_WHISPER_*` and config section `[llm.localWhisper]`.

### Warning codes
- `STT_LOCAL_MODEL_MISSING`, `STT_LOCAL_INIT_FAILED`, `STT_LOCAL_INFERENCE_FAILED`.

### Schema
- `request.schema.json` `llmProvider` enum extended with `local-whisper`.

## [0.2.0] — Initial public release

### Added
- CLI + MCP server entrypoints.
- Source ingestion: yt-dlp (~1000 sites), local files, Playwright-driven browser capture
  (SharePoint Stream, Microsoft Stream, Medius).
- Captions: VTT / SRT extraction; multi-language preferences; network-level caption
  interception in browser-capture mode.
- STT: cloud-only (GitHub Copilot SDK default; OpenAI Whisper via `/audio/transcriptions`).
- OCR: structured JSON (`freeText`, `lines[]`, `tables[]`). Two backends — `local` (RapidOCR
  PP-OCRv5 latin via ONNX, default) and `copilot` (LLM vision-as-OCR).
- Vision: structured JSON (`kind`, `title`, `bullets[]`, `codeBlocks[]`, `charts[]`,
  `uiElements[]`, `freeText`).
- Speaker attribution from VTT `<v>` tags and SRT prefixes (no diarization in this release).
- Slide grouping via 64-bit dHash perceptual hashing; chapter detection (deterministic
  lexical); cross-modal alignment (`evidence-aligned/by-{slide,chapter}.json`).
- Search: three backends — `json` (TF-IDF), `sqlite` (FTS5 / BM25), `sqlite-onnx` (FTS5 +
  local ONNX embeddings, `Xenova/all-MiniLM-L6-v2`).
- Clip extraction with timestamped MP4 output.
- Persistent queue worker (`queue enqueue|run|status`); MCP background jobs.
- `doctor` and `deps install` commands.
- Stable JSON Schemas for every machine-readable artifact (~16 files in `schemas/`).

[0.7.0]: https://github.com/MoaidHathot/Zakira.Replay/releases/tag/v0.7.0
[0.6.0]: https://github.com/MoaidHathot/Zakira.Replay/releases/tag/v0.6.0
[0.5.0]: https://github.com/MoaidHathot/Zakira.Replay/releases/tag/v0.5.0
[0.4.0]: https://github.com/MoaidHathot/Zakira.Replay/releases/tag/v0.4.0
[0.3.0]: https://github.com/MoaidHathot/Zakira.Replay/releases/tag/v0.3.0
[0.2.0]: https://github.com/MoaidHathot/Zakira.Replay/releases/tag/v0.2.0
