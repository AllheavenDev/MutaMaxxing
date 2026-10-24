# Contributing

Bug reports, ideas, and pull requests are welcome.

## Report a problem

Open an issue with the Dalamud version, steps to reproduce the problem, and any relevant sanitized logs. For a feature request, describe what you were trying to do and what you expected to happen.

## Send a change

1. Fork the repository and make your change on a branch.
2. Keep the pull request focused and explain the user-facing change.
3. Include the checks you ran. If the change affects in-game behavior, say whether you tested it in game.

## Build

Install the .NET SDK required by `MutaMaxxing.csproj` (currently .NET 10), then run this from the repository folder:

```powershell
dotnet build .\MutaMaxxing.csproj --configuration Debug
```

There is no automated test project yet. A successful build does not verify behavior in FFXIV.
