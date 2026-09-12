# Contributing

Thanks for taking the time to contribute!

## Development setup

```bash
git clone https://github.com/s4ndr0ne/Workbench.git
cd Workbench
dotnet build Workbench.slnx
dotnet test Workbench.slnx
dotnet run --project samples/Workbench.Sample   # then open http://localhost:5000/workbench
```

The dashboard lives in a single file, `src/Workbench/wwwroot/index.html`, embedded into the assembly at build time. No bundler, no npm: edit, rebuild, refresh.

## Releasing

Releases are tag-driven:

```bash
# 1. Move the "Unreleased" notes in CHANGELOG.md under a new "## [x.y.z] - YYYY-MM-DD" heading
# 2. Commit, then tag and push
git tag v0.2.0
git push origin v0.2.0
```

The `Release` workflow builds, tests, packs with the tag version, and creates a GitHub Release with the changelog section, `.nupkg`, and `.snupkg` attached. Download the packages from the GitHub Release and publish them manually to the desired registry.

## Pull requests

- Keep changes focused; one topic per PR.
- Add or update tests in `tests/Workbench.Tests`.
- Update `CHANGELOG.md` under `## [Unreleased]`.
