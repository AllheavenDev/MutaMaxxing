# Contributing to MutaMaxxing

MutaMaxxing is a Dalamud plugin for configuring and running Mutamix materia transmutation. Bug reports and focused pull requests are welcome.

## Development setup

1. Clone `https://github.com/AllheavenDev/MutaMaxxing.git`.
2. Install the .NET 10 SDK and Dalamud development files for the API level in `MutaMaxxing.csproj`.
3. In this workspace, run `./build-local.ps1` from the repository root. It builds Debug and stages the plugin into the sibling `TouristMod-AutoTour/local-plugin/` directory. A standalone clone can use `dotnet build .\MutaMaxxing.csproj --configuration Debug` after its SDK and Dalamud development references are available.

The project uses `Dalamud.NET.Sdk/15.0.0` and currently targets .NET 10. Check the project and lock file rather than assuming those versions will remain current.

## Code style and architecture

- Follow `.editorconfig`: four spaces in C#, file-scoped namespaces, braces, and the established `var` conventions.
- Keep UI in `Windows/` and game/integration services in `Services/`; prefer narrow services with explicit ownership and disposal.
- Use an explicit, cancellable update-driven state machine for multi-step game actions. Validate live state before consuming inventory or submitting a trade.
- Handle unavailable IPC providers, busy states, timeouts, and cancellation. Treat a successful IPC call as request acceptance, then verify completion from observable state.
- Avoid adding dependencies or unrelated refactors without a clear reason.

## Validation

- Run `build-local.ps1` for local Debug build and staging when available.
- If changing the release path, inspect the output of the Release workflow and verify the ZIP contains the plugin DLL, manifest JSON, and required dependency files at the archive root.
- There is no dedicated test project in this repository at present. State exactly which build or checks ran.
- In-game checks are separate from compilation: verify the Materia Transmutation window, safe stopping, inventory tracking, TextAdvance restoration, and Lifestream/vnavmesh travel when a change affects those paths.

## Branches, commits, and pull requests

- Make changes on a focused branch and open a pull request to `main`. Do not commit directly to `main`.
- Use Conventional Commit subjects such as `feat:`, `fix:`, `refactor:`, `docs:`, `test:`, `chore:`, `perf:`, or `ci:`.
- Agent-created commits in this repository use `Shikaare <shikaare@gmail.com>` configured locally in this repository. Do not change global Git settings or add AI co-author trailers.
- This identity requirement applies to the repository owner and agents working on their behalf. External contributors should retain their own author and committer identities. GitHub authentication is separate from commit identity.
- Keep each PR focused. Describe the behavior or documentation change, list checks performed, and identify any live/in-game validation that remains.
- Do not force-push, rewrite history, change repository visibility, or publish a release without explicit authorization.

## Issues and feature requests

For bug reports, include the Dalamud/API version, relevant plugin versions, concise reproduction steps, and sanitized log excerpts. Do not attach credentials, account data, or unrelated personal information. Feature requests should describe the user problem and the expected behavior.
