# Contributing to Halim Recovery

Thank you for considering a contribution!

## Ground rules

1. **Recovery correctness beats features.** Never mark an untested recovery capability
   as working. Every scanner/carver change must pass the test laboratory
   (`testlab/Run-TestLab.ps1`) with no regressions.
2. **The source volume is read-only.** No code path may ever write to the volume being scanned.
3. **No invented confidence.** Health/confidence values must be derived from measurable
   evidence (allocation state, structure validation). Document the evidence in `HealthNotes`.
4. **No GPL code.** The project is MIT; write parsers from public format specifications.
   New dependencies must be MIT/BSD/Apache-2.0 and documented in THIRD_PARTY_NOTICES.md.
5. **Honest UX.** Never present an unsupported filesystem or an unverifiable recovery as supported/successful.

## Development setup

```powershell
# Requirements: .NET 9 SDK, Windows 10/11
dotnet build HalimRecovery.sln
dotnet test HalimRecovery.sln          # unit tests
# End-to-end recovery validation (needs admin, uses virtual disks only):
powershell -ExecutionPolicy Bypass -File testlab\Run-TestLab.ps1
```

## Project layout

- `src/HalimRecovery.Core` — recovery engine (no UI dependencies). See `docs/ARCHITECTURE.md`.
- `src/HalimRecovery.App` — WPF GUI
- `src/HalimRecovery.Cli` — command-line frontend
- `tests/` — xUnit unit tests
- `testlab/` — VHD-based end-to-end recovery tests

## Pull requests

- Keep changes focused; one topic per PR.
- Add unit tests for parsers/validators; run the test lab for engine changes and include
  the benchmark table in the PR description.
- Follow existing code style (file-scoped namespaces, nullable enabled).
