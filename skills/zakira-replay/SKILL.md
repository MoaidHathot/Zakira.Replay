---
name: zakira-replay
description: Deprecated combined Zakira.Replay skill. Prefer zakira-replay-cli when shell commands are available, or zakira-replay-mcp when Zakira.Replay MCP tools are available.
---

# Zakira.Replay Skill Router

This combined skill is retained for compatibility. Prefer the focused skills:

- `zakira-replay-cli`: use when the agent can run shell commands.
- `zakira-replay-mcp`: use when Zakira.Replay MCP tools are available.

If this is the only available Zakira.Replay skill, choose the surface you can access:

- Shell available: follow `../zakira-replay-cli/SKILL.md`.
- MCP tools available: follow `../zakira-replay-mcp/SKILL.md`.

Core rule: never pretend you watched a video directly. Use Zakira.Replay to create durable artifacts, then answer from `manifest.json`, `evidence.json`, `transcript.md`, frame images, `ocr/combined.md`, `vision/combined.md`, `summary.md`, and `chapters/chapters.md`.
