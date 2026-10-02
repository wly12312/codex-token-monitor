# Contributing

Codex Token Monitor is a Windows WPF application. Install the .NET 8 SDK and run
`./scripts/Build.ps1` and `./scripts/Test.ps1` from PowerShell. The scripts find
the project relative to their own location, so the checkout can live anywhere.
Before a commit, run `./scripts/Verify-PublicFiles.ps1 -Staged` after staging
your files. It checks Git's actual list and reports filenames without printing
possible secrets. A passing pattern scan still needs a human review of new data.

The parser regression checks generate synthetic JSONL logs in `artifacts/`.
Do not upload real Codex sessions, authentication files, SQLite logs, task names,
or prompts to an issue or pull request. A small synthetic example with the same
record structure is usually enough to reproduce a parsing bug.

For a bug, include Windows and .NET versions, the monitor version, expected
behavior, and a synthetic reproduction. For a new log format, add a focused
parser regression check. Keep unknown models and unavailable evidence explicit;
do not infer actual subscription billing or model quality from local logs.

The UI is Windows-only. Missing optional SQLite model evidence must continue to
show as unverified. Changes must preserve read-only access to Codex data and
must not add a network dependency to normal application startup.

The root `README.md` is the canonical Chinese documentation. Keep
`source/README.md` synchronized because it is included in build output. The
English introduction is in `README.en.md`.
