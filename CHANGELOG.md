# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.3.0] - 2026-09-11

### Added

- **PDF PageCount**: Imported PDFs store page count in `Items.PageCount` (old DBs migrate automatically)
- **Selective PDF vision**: `sip --show <id> --json --vision --pages 3-7` (also `1,5,9`, `-10`, `50-`) rasterizes only the requested pages for agents / vision models
- **PDFtoImage** dependency for page rasterization (replaces ad-hoc image scrap for PDFs)

### Changed

- **publish.ps1**: Stage to a temp path first, then copy into `publish/` — fixes MSB3094 when the project path contains an apostrophe (`hahahotsoup's`)
- **README**: Dropped the DeepSeek pricing notice

### Deprecated

- **TUI**: Still available, but will be phased out. New investment is CLI + Web (sip-web). Prefer `sip` flags and the local web UI going forward; TUI removal timing will be announced in a future Release Note.

## [1.1.0] - 2026-08-01

### Added

- **RSS Feed Management**: Add, remove, and update RSS subscriptions
- **Article Reading**: TUI interface for reading articles
- **Full-text Search**: Search articles by keywords
- **Semantic Search**: AI-powered similarity search
- **Today's Soup**: Daily recommendations
- **Telemetry**: Optional local reading behavior tracking

### Changed

- Initial release with core RSS reading functionality

## [1.0.0] - 2026-07-15

### Added

- **Core Architecture**: SQLite-based local-first design
- **Feed Management**: RSS feed subscription and management
- **Article Storage**: Article content and metadata storage
- **Basic Search**: Simple text search functionality

### Changed

- Initial release
