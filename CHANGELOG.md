# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Planned
- "Download entire chat/topic" that walks the full message history (current "Download all"
  covers loaded messages only). See `Planning.md`.
- Folder schema hardening (`{id}_{title}`) to avoid collisions between identically named chats.
- Richer sender resolution in generated documentation.
- A single, consistent English UI.

## [1.0.0] - 2026-06-13

### Added
- Telegram login (api_id / api_hash, verification code, optional 2FA) with persisted encrypted
  session.
- Browse chats, supergroups, channels, and forum topics; search messages.
- Media download: per file, per album, and "Download all" for loaded messages, via a
  sequential, rate-limit-friendly queue with `FLOOD_WAIT` handling.
- Strict, portable download root with a per-root SQLite manifest (`telegrab.db`); relative media
  paths and automatic cache/resume when reopening an existing root.
- Media viewer with photo/video preview, zoom and pan (1x–8x), and a filmstrip.
- Per-folder `README.md` documentation generated from the manifest, with marker-preserving
  regeneration, in-app Markdown preview (Markdig), an editor with live preview, and
  external-viewer compatibility.
- Caption association (own / album / reply / inferred / none) with cross-page reply resolution.

[Unreleased]: https://github.com/OWNER/Telegrab/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/OWNER/Telegrab/releases/tag/v1.0.0
