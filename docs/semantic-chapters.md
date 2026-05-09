# Semantic Chapter Detection

Semantic chapter detection means grouping video evidence into meaningful sections such as introduction, setup, demo, benchmark, Q&A, or conclusion.

Zakira.Replay implements deterministic offline lexical chapter generation. Embedding-backed and LLM-refined chapters remain future enhancements.

## Inputs

Chapters can be inferred from:

- Transcript segment timestamps and text.
- Existing captions or sidecar subtitles.
- Scene-change frame timestamps.
- OCR text from slides or dashboards.
- Vision descriptions of frames.
- Metadata such as title, duration, and webpage URL.

## Practical Algorithm

1. Build candidate boundaries from transcript gaps, scene-change frames, slide title OCR, and topic-shift signals.
2. Create evidence windows around each candidate boundary.
3. Score topic shifts between neighboring windows using lexical similarity, embeddings, or an LLM.
4. Merge short adjacent sections and split sections that exceed a max duration.
5. Generate chapter labels from the transcript/OCR/vision evidence in each section.
6. Write `chapters.json` and `chapters.md` with timestamp ranges, labels, summaries, and supporting evidence references.

## Implementation Options

### Offline Lexical

Use the local search/index tokenization and cosine similarity to detect large topic shifts between transcript windows.

Pros:

- No network or model calls.
- Deterministic and testable.
- Good enough for lectures and tutorials with clear transcript changes.

Cons:

- Weak on subtle semantic changes.
- Poor when transcripts are sparse or noisy.

### Embedding-Backed

Use embedding vectors over transcript/OCR/vision windows and detect cosine-distance spikes.

Pros:

- Better semantic grouping.
- Works across paraphrases and vocabulary changes.

Cons:

- Requires an embedding provider and cache.
- Adds provider/version reproducibility concerns.

### LLM-Backed

Ask the LLM to produce chapters from bounded evidence windows.

Pros:

- Best labels and summaries.
- Can combine transcript, OCR, and vision evidence naturally.

Cons:

- Slower and more expensive.
- Requires careful chunking and evidence citation to avoid hallucinated chapters.

## Current Implementation

Build chapters with:

```bash
zakira-replay chapters build <run-directory> --min-duration 60 --max-duration 600
```

The build command produces:

- `chapters/chapters.json`
- `chapters/chapters.md`

The current algorithm:

1. Reads transcript segments from `evidence.json`.
2. Groups transcript text into time windows.
3. Scores lexical topic shifts between adjacent windows.
4. Splits when the topic shift is high and the minimum duration is met, or when the maximum duration is reached.
5. Generates deterministic labels and evidence references from transcript/OCR/vision/frame artifacts.

Future refinements can add:

```bash
zakira-replay chapters refine <run-directory> --model gpt-5.5
```

Suggested schema:

```json
{
  "schemaVersion": "0.1",
  "runId": "example-run",
  "chapters": [
    {
      "startSeconds": 0,
      "endSeconds": 120,
      "title": "Introduction and goals",
      "summary": "The speaker introduces the router comparison and evaluation criteria.",
      "evidence": [
        { "kind": "transcript", "timestamp": "00:12", "text": "..." },
        { "kind": "frame", "timestamp": "01:05", "path": "frames/frame-001.jpg" }
      ]
    }
  ]
}
```
