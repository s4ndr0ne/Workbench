# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed
- The NuGet package ID is now `s4ndr0ne.Workbench`.
- Package publishing is now manual; releases only attach the generated packages to the GitHub Release.

## [0.1.0] - 2026-09-12

### Added
- `AddWorkbench()` / `UseWorkbench()` extension methods.
- Embedded single-page dashboard served at `/workbench` (configurable).
- Live runtime metrics (CPU, working set, managed heap) with rolling history.
- Health checks report integration.
- In-memory request log with body capture, streamed in real time over Server-Sent Events.
- API explorer that discovers mapped endpoints and lets you invoke them from the dashboard.
- Redesigned dashboard: tabbed navigation, light/dark theme, request-log filtering, JSON highlighting, chart tooltips.
- Multi-targeting for `net8.0`, `net9.0` and `net10.0`.

### Fixed
- Request bodies sent with chunked transfer encoding (no `Content-Length`) are now captured.
- `CaptureRequestBody = false` is honoured.
- CPU usage is normalised by core count (previously could exceed 100% on multi-core hosts).
- Metrics history length now follows `MetricsHistory`.
- Metrics sampling starts with the host instead of on the first dashboard request; uptime uses the real process start time.
- Managed heap size no longer reads 0 MB before the first garbage collection.
- Dashboard escapes all server-provided strings before rendering.
- CI workflow (build + test on Linux/Windows) and tag-driven workflow with downloadable GitHub Release artifacts.

[Unreleased]: https://github.com/s4ndr0ne/Workbench/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/s4ndr0ne/Workbench/releases/tag/v0.1.0
