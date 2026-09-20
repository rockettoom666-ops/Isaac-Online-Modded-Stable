# Co-op stability preview

This fork contains targeted fixes and **opt-in compatibility workarounds**, not a
replacement netcode implementation. A two-PC game session has not been tested.
The reported disconnect after holding R is still unverified.

## 1.4.1-preview.4

The latest J460 log reports a player `PostVelocity` mismatch at frame 121,
with equal global RNG and save checksums. It contains no CuerLib Lua exception.
This does not establish the cause or prove that either peer applied the fixes.
Discarding duplicate input packets is not evidence of broken networking.

- Reset CuerLib's input history and detection cooldown at each game start so
  input samples from a previous run cannot identify the new run's local player.
- Extend EID's co-op input protection to Bag of Crafting recipe browsing;
  descriptions and recipe display remain available.
- Correct Coming Down's entities2.xml version from 1 to 5, as documented in the
  [entity example](https://isaacblueprints.com/tutorials/crash_course/entity_basics/).
  This XML correction is not a demonstrated fix for the velocity mismatch.
- Report a wrong/empty mods folder separately from already installed fixes.
- Show the patcher version and add **Export mod report...**: per-file rule
  status and Lua/XML hashes for comparing both PCs. Mod save/config data and
  the game's actual loaded state are not verified by this report.

Upgrade from preview.3 by extracting the new release separately and applying
the safety button to the active game's `mods` folder on both PCs with the game
closed. No save restoration or Steam file verification is needed for this upgrade.
`EID Patched: Yes` refers only to the separate legacy EID patch.
These additional issues are covered by mock tests; a two-PC reproduction is
still needed to determine whether they affect the reported disconnect.

The new **Apply mod safety workarounds...** button accepts the game's `mods`
folder or a Workshop `content/250900` directory. It identifies CuerLib, EID, MCM
Impure, emote-binds and Specialist by metadata Workshop ID, validates all supported files
before writing, preserves UTF-8 BOM/newlines, and saves exact `.iom-<sha256>.bak`
backups. Unknown code is rejected. Close the game and finish Workshop updates
first. Install the same changes on each peer and restart the game.

Changes:
- Guard the confirmed `Coming Down! [Rework]` `ZoneLink` cleanup against nil
  data; the user's log showed this `PostUpdate` error during the affected run.
- Clear emote-binds scheduled callbacks at game start, including R restarts.
- Correct Specialist's player indices from 1..N to 0..N-1.
- Correct CuerLib's zero-based player index before using Lua's one-based
  `table.remove`/`table.insert`; the unguarded call was confirmed at
  `cuerlib/class/netcoop.lua:279` immediately before the reported desync.
- Block EID's local recipe-search entry/input suppression, MCM's menu/input
  overrides and emote keybinds when `GetNumPlayers() > 1`.
- Remove Antibirth Music+++'s unused gameplay `Random()` call on room changes
  and fix the music callback's out-of-range player access.
- Validate co-op and analytics EXE signatures together before writing.

The player-count guard intentionally includes local co-op and single-player
characters with extra player entities. It is a feature restriction, **not
synchronized search, menus or emotes**. Descriptions remain available. The old
EID stage patch is a separate button. Music changes are installed separately.
Other gameplay mods are not certified compatible; matching mods/settings is
necessary for many mods but insufficient to prove determinism.

Restore the appropriate `.bak` over its corresponding Lua file with the game
closed, or reinstall the mod. Backups apply only to the new safety button.
Restore the original EXE through Steam's file verification. Workshop updates may
overwrite patches; rerun preflight after updates. Do not restore a backup from
an older incompatible mod release.

See [Russian installation and reproduction guide](STABILITY.ru.md) for the R
restart test matrix, known limits, rollback and test commands. Run
`dotnet run --project tests/Regression` for patch-engine tests and
`python tests/test_lua.py` with `lupa==2.8` for Lua mocks. Additional Lua behavior
checks require copies of the supported installed mods; no third-party mod
sources or game binaries are redistributed as test fixtures.

Evidence: upstream reports [#36](https://github.com/xADDBx/Isaac-Online-Modded/issues/36)
and [#17](https://github.com/xADDBx/Isaac-Online-Modded/issues/17) concern music RNG;
[#7](https://github.com/xADDBx/Isaac-Online-Modded/issues/7) and
[#14](https://github.com/xADDBx/Isaac-Online-Modded/issues/14) concern EID input.
Additional code was inspected from installed Workshop versions. These reports
support targeted investigation, not a claim that every desync is solved.
