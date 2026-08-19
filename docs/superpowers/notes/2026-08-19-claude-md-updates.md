# CLAUDE.md updates applied to the external overlay (2026-08-19)

`CLAUDE.md` at the repo root is a symlink into a personal configuration overlay that lives
outside this repository, kept out of its git history on purpose (`.gitignore`: `# Personal Claude
Code config that lives alongside project repos via symlink ... Keep it out of public/open-source
commits.` — `CLAUDE.md` is one of the patterns listed). Because that overlay is not versioned
here, this note is the only record in this branch that the documentation half of Task 11 happened.

Two amendments were applied directly to the overlay's copy of the file:

## 1. Testing section corrected

It previously described a single test project (`tests/TriffView.Tests`) with a `<Compile
Include>` / Windows-free constraint, stated as if it were the whole picture. There are in fact
**three** test targets, verified against the actual CI workflow files and project structure:

| Project | Framework | Style | CI |
|---|---|---|---|
| `tests/TriffView.Tests` | plain `net8.0` | `<Compile Include="..\..\native\TriffView\Foo.cs" />` links pure-logic source files rather than a `ProjectReference`, Windows-free | `.github/workflows/build.yml:29-30` |
| `native/TriffView.Tests` | `net8.0-windows` | `ProjectReference` + `InternalsVisibleTo`, 27 test files (incl. `TriffAudio`, which is Windows-only) | `.github/workflows/ci.yml:49-55` |
| `tests/TriffAlerts.Tests` | `net8.0` | regression harness via `dotnet run`, not `dotnet test` | `.github/workflows/ci.yml:58` |

The Windows-free `<Compile Include>` constraint applies only to the first. The file now says so
explicitly and explains that `native/TriffView.Tests` is the target for anything needing real
Windows types or `internal` access instead of an extracted pure helper.

## 2. New "Audio splash detection (`native/TriffAudio/`)" section added

Placed after "Coordinate spaces" and before the (rewritten) "Testing" section, matching the
existing pattern of a dedicated section per tricky subsystem. Covers, each verified against the
actual code rather than assumed:

- The feature-generation and normalisation constants read directly from `SplashFeatures.cs`
  (16 kHz, FFT 1024, hop 85, 32 bands over `geomspace(80, 7600)`, 2.0 s/376-frame window,
  30 s/5647-frame context, MAD floor `1e-3`) — changing any of *these* invalidates the 11
  committed template assets under `native/TriffAudio/Assets/splash-templates/`, because they
  determine how those templates were generated.
- Separately, the default threshold (0.35, `TriffAudioService.cs`) is a runtime comparison against
  a score. It changes sensitivity only; it plays no part in generating templates and changing it
  invalidates nothing.
- The template/gain contract (`SplashTemplate.cs:82` divides samples by `stats.Gain`) and why
  skipping it breaks scoring.
- The muted-client silent-failure mode: `AUDCLNT_BUFFERFLAGS_SILENT` is never set on a muted
  process's zero-filled buffers, so silence detection counts non-zero samples instead
  (`WasapiProcessCapture.cs`, `AudioRingBuffer.cs`, `TriffAudioService.cs`).
- The 30 s warm-up requirement (`TriffAudioService.cs:39-40`: 3 s of context puts both classes in
  the 0.22-0.24 score band, indistinguishable).
- The no-hardcoded-PID testing rule and the `IAudioCapture` seam
  (`WasapiProcessCapture.cs:13`, used by `native/TriffView.Tests/TriffAudioServiceTests.cs`).

## What did not change

The Architecture section's one-line subsystem list gained a `TriffAudio` mention, matching how
the other three subsystems are named there. Nothing else in the file was touched — voice,
structure, and every other section are unchanged.
