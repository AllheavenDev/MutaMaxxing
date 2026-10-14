# MutaMaxxing

Dalamud plugin for batch-transmuting selected materia with Mutamix.

## Features

- Filter eligible materia by category, grade, or item; blacklist rules always win.
- Keep every trade to one materia grade, with optional five-type batches and fallback.
- Track materia received during and after a run.
- Optionally route to Mutamix with Lifestream and vnavmesh.
- Temporarily manage TextAdvance during a run.

## Use

Open `/mutamaxxing`, choose your rules, then open Mutamix's Materia Transmutation window and start nearby. Stop from the window or with `/mutamaxxing stop`.

## Requirements

- Dalamud
- TextAdvance for dialogue skipping during runs
- Lifestream and vnavmesh for the route button

## Link to insert into Dalamud Experimental

```text
https://raw.githubusercontent.com/AllheavenDev/MutaMaxxing/main/pluginmaster.json
```

In Dalamud, open `/xlsettings` → **Experimental** → **Custom Plugin Repositories**, paste the link, save it, and refresh the Plugin Installer. The feed is available now. Installation and updates will work after a GitHub release publishes the `MutaMaxxing.zip` package referenced by the feed.

## About and contributions

This started as a plugin I made for myself and is 100% vibe coded. Ideas and optimizations are welcome as issues or pull requests. See [AI_USAGE.md](AI_USAGE.md) for the AI usage note.

## License

Licensed under the MIT License. See [LICENSE](LICENSE).

## Repository

[AllheavenDev/MutaMaxxing](https://github.com/AllheavenDev/MutaMaxxing)
