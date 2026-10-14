# MutaMaxxing

An experimental Dalamud plugin that previews eligible materia in the player's four inventory bags and can batch-submit Mutamix transmutations.

## Research

- Square Enix describes Materia Transmutation as exchanging five materia for one materia. The unlock quest is **Marvelously Mutable Materia**, available from Kokosamu in Central Thanalan after **Life, Materia, and Everything**: [Patch 2.25 notes](https://na.finalfantasyxiv.com/lodestone/topics/detail/506a135c96590f304f18e8c110b45897e3a016cf).
- Square Enix says the Mutamix transmutation cutscene can be skipped after it has been viewed once: [Patch 5.0 notes](https://na.finalfantasyxiv.com/lodestone/topics/detail/330f2b280067d69d85b17831c66712a499e97484).
- TextAdvance provides IPC for external control of talk skipping and cutscene skip settings. MutaMaxxing requires TextAdvance to be installed, enabled, and available through IPC, then requests those settings only while a run is active: [TextAdvance repository](https://github.com/NightmareXIV/TextAdvance) and [IPC provider](https://github.com/NightmareXIV/TextAdvance/blob/master/TextAdvance/Services/IPCProvider.cs).
- The closest public implementation found is Daily Routines' `AutoMateriaTransmutation` module. It confirms the `TradeMultiple` addon and native transmutation agent are the game-facing integration points. Daily Routines is AGPL-3.0; its code is used only as a behavior/API reference and is not included or copied here: [module source](https://github.com/Dalamud-DailyRoutines/DailyRoutines.ModulesPublic/blob/main/Interface/AutoMateriaTransmutation.cs), [repository license](https://github.com/Dalamud-DailyRoutines/DailyRoutines.ModulesPublic/blob/main/LICENSE).
- The current Lumina `Materia` schema exposes item references as a grade-indexed array. MutaMaxxing reads populated entries at runtime, so its grade list is data-driven and new grades can appear without changing a hardcoded I–XII list: [EXDSchema Materia definition](https://raw.githubusercontent.com/xivdev/EXDSchema/latest/Materia.yml).

## Current behavior

- The preview shows materia and quantities found in Inventory 1–4.
- Whitelist mode is the default, so nothing can be traded until you select materia. The alternate mode trades all discovered materia except blacklist rules.
- Whitelist mode trades only selected category/grade rules and individually whitelisted materia.
- Blacklist rules always take precedence. The rule matrix supports Battle, Crafter, Gatherer, and Other; “Any tier” also covers future grades.
- Every batch uses materia from one grade only; grades are never mixed, even when the distinct-materia option is off. The controller picks a grade that has enough eligible inputs for a complete trade.
- Optional **Require five different materia of one grade** mode selects one materia from each of five distinct item types at the same grade. A type can be used in later trades.
- MutaMaxxing adds a random 5–10 ms micro-delay between materia selection requests and waits 10 ms before confirming each batch.
- Optional **Add randomized delay between trades (50–70 ms)** adds a random pause averaging 60 ms after each completed trade, on top of the existing completion wait.
- `/mutamaxxing` opens the menu for selecting eligible materia and starting a run. The Start button is enabled only when Mutamix is within 5 yalms, the `TradeMultiple` window is open, TextAdvance is enabled and responding, and the game window has no materia already selected. Moving out of range during a run stops the next trade.
- Each exchange uses five inputs. MutaMaxxing waits for those inputs to leave inventory before choosing another batch. A timeout stops the run without retrying a possibly submitted exchange.
- Use the **Stop run** button or `/mutamaxxing stop`. A submitted in-game exchange may finish after Stop; no later exchange will be submitted.

## Build and local loading

The project targets Dalamud API 15 and .NET 10, matching the current local plugin examples in the parent workspace.

```powershell
.\build-local.ps1
```

The helper builds a Debug plugin and stages `MutaMaxxing.dll` plus its manifest and dependency metadata into the shared local plugin folder:

```text
<workspace>\TouristMod-AutoTour\local-plugin\
```

That is the same folder used by TouristMod and AnoMech. The helper removes or replaces only `MutaMaxxing.*` files, preserving the other plugins. This workspace's Dalamud config now lists `MutaMaxxing.dll` as a Dev Plugin Location. For another Dalamud profile or machine, add the full DLL path printed by the helper to **Settings → Experimental → Dev Plugin Locations**. Refresh the dev plugin list or restart Dalamud after building. The helper prefers the .NET 10 SDK and package cache used by the sibling TouristMod project, then falls back to the `dotnet` on PATH.

## In-game test loop

1. Load the dev build and confirm `/mutamaxxing` opens the selection menu. Check that its Mutamix distance readout and Start button update while moving toward and away from him.
2. Stand within 5 yalms, open Mutamix's Materia Transmutation window, enable TextAdvance, and compare the preview against the materia in Inventory 1–4. Confirm Start remains disabled until all prerequisites are met.
3. Start with at least five low-value materia that match the active rules. Confirm that the game window begins with no selected materia.
4. Observe one submitted exchange and verify five inputs leave inventory, one result appears, and the next batch is not submitted before the inventory change is observed.
5. Verify trades never mix materia grades. Enable the five-different-materia option and confirm each batch also contains five distinct types of that grade, including when duplicate types are split across inventory stacks. Confirm Start is disabled unless one grade has five eligible types.
6. Enable the randomized inter-trade delay and observe that it adds a small pause after a completed trade.
7. Test Stop while selecting and after submission, then test the stop command, moving out of Mutamix's range, closing the Mutamix window, missing/disabled TextAdvance, and fewer than five eligible items.
8. Repeat against the current game build and revise the state machine based on observed addon and inventory behavior.

The source has not yet been loaded in game. No live materia exchange has been performed by MutaMaxxing. The game-facing agent and inventory calls are patch-sensitive and need the in-game loop above before release.

## Repository notes

The source repository is private at [AllheavenDev/MutaMaxxing](https://github.com/AllheavenDev/MutaMaxxing). A custom Dalamud repository feed is not available while the source stays private: Dalamud fetches custom repository JSON without authentication, and each entry must point to a downloadable plugin ZIP. Use the local dev plugin folder above for now. A distribution license has not been selected yet; decide that before making the repository public or publishing binaries. See [Dalamud's custom repository guide](https://dalamud.dev/plugin-publishing/custom-repositories/) for the feed and release format.
