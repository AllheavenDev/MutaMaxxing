# MutaMaxxing Agent Instructions

These rules apply throughout the MutaMaxxing repository. When it is inside the local Dalamud plugin development workspace, also follow the workspace-level `../AGENTS.md`.

## Delegation and repository workflow

- Do not use subagents unless the user explicitly asks for one.
- Work on a focused branch and submit repository changes through a pull request. Do not commit directly to `main`.
- Use Conventional Commits and separate unrelated changes.
- Before committing owner-maintained changes, verify that this repository's local Git identity is configured for Shikaare. Keep it repository-local; never change global Git configuration or add AI co-author trailers.
- External contributors should use their own author and committer identities. GitHub authentication is independent, so contributors do not need to change how they sign in.
- Never rewrite history, force-push, delete refs, publish a release, or change repository visibility without explicit user authorization.
- The canonical repository is `https://github.com/AllheavenDev/MutaMaxxing`. Preserve its current visibility unless the user explicitly requests a visibility change.

## Project conventions

- Read `MutaMaxxing.csproj`, `packages.lock.json`, `MutaMaxxing.json`, `.editorconfig`, and `build-local.ps1` before changing build, dependency, or manifest behavior.
- Follow `.editorconfig`: four spaces for C#, file-scoped namespaces, braces, and the existing `var` conventions. Keep services focused and follow the existing dependency-injection and `IDisposable` patterns.
- Keep inventory selection and trade submission conservative. Recheck selected slots and rules immediately before a trade; never mix materia grades within one batch; blacklist rules take priority; stopping must prevent another trade even if an already-submitted trade finishes.
- Keep TextAdvance control temporary and release it on stop/disposal. Do not persistently overwrite the user's TextAdvance settings.
- Keep travel as a cancellable state machine. Check Lifestream/vnavmesh availability and busy state, wait for observable completion with timeouts, and make route/run controls mutually exclusive. An IPC request being accepted is not proof that the player arrived or interacted.
- Native addon/client-struct access and IPC contracts are patch-sensitive. Verify current Dalamud, FFXIVClientStructs, TextAdvance, Lifestream, and vnavmesh APIs before changing them.
- Preserve unrelated projects and files in the parent workspace, especially existing dirty worktrees.

## Build and validation

- `build-local.ps1` builds the Debug plugin and stages its files into the sibling `TouristMod-AutoTour/local-plugin/` directory. It removes only files named `MutaMaxxing.*` from that destination; retain this isolation.
- API 15 uses `Dalamud.NET.Sdk/15.0.0` and requires .NET 10. The helper prefers the sibling `TouristMod-AutoTour/.dotnet10/dotnet.exe`; system .NET 8 is not sufficient.
- The repository currently has no dedicated test project. Use the build helper for local staging and inspect the Release workflow/package path when modifying release behavior.
- Separate build/package success from in-game UI, transmutation, TextAdvance, and live route verification. Do not claim the route was tested in game unless it was.
- The Release workflow creates the archive with `Compress-Archive`; `DalamudPackager.targets` suppresses the SDK ZIP step because it failed with access denied in this workspace. Recheck both manifest generation and release archive contents before changing this arrangement.
- `pluginmaster.json` is a feed artifact; the plugin manifest inside the ZIP and the feed are distinct files. Keep their author, version, API level, repository URL, and download URLs consistent.
