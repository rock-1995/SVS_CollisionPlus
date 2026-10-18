# SVS Collision Plus

Soft breast collisions for **Summer Vacation Scramble (SamabakeScramble)**, focused on reducing breast–belly clipping when pregnancy deformation enlarges the abdomen. The plugin adds a soft, limited correction while keeping the game's native breast motion.

Based on [AL Collision Plus](https://github.com/rock-1995/AL_CollisionPlus).

## Download and installation

Download **SVS_CollisionPlus_0.1.4.zip** from [Releases](https://github.com/rock-1995/SVS_CollisionPlus/releases/latest).

1. Close the game completely.
2. Merge the archive's `BepInEx` folder into the game directory.
3. Start the game. The installed file should be `BepInEx/plugins/SVS_CollisionPlus/SVS_CollisionPlus.dll`.

Requires **BepInEx 6 IL2CPP**. Developed and checked against SamabakeScramble v1.1.5P1 and its native MagicaCloth implementation. Belly shape remains controlled by [SVS Pregnancy](https://github.com/rock-1995/SVS.Pregnancy.Plugin.dll) or another deformation plugin.

To upgrade, replace the DLL and keep your collision settings. **If you used the 0.1.3 diagnostic build, set `CaptureEvidence=false`.** An existing saved value of `true` is not reset when the DLL is upgraded.

To uninstall, delete the plugin DLL.

## Settings

With BepInEx Configuration Manager installed, press **F1** and search for `SVS Collision Plus`. Alternatively, close the game and edit `BepInEx/config/local.svs.collisionplus.cfg`.

| Setting | Purpose | Default |
| --- | --- | --- |
| `Enabled` | Master switch; disabling clears this plugin's added displacement | On |
| `BreastAbdomen` | Soft breast–abdomen contact | On |
| `BreastBody` | Breast contact with other body regions | Off |
| `BreastGround` | Breast contact with scene support surfaces | Off |
| `CaptureEvidence` | Full diagnostic snapshots for offline analysis; adds overhead | Off |

Start with the default tuning. If needed:

- `BreastAbdomenStrength`: correction strength.
- `BreastAbdomenMaxOffsetHeightRatio`: maximum added displacement.
- `BreastAbdomenCompressionHeightRatio`: allowed soft compression; smaller values are stricter.
- `BreastAbdomenMaxSpeedHeightRatio` and `BreastAbdomenResponsePerSecond`: response speed.

## Performance in 0.1.4

The nearest-surface search now visits closer branches first and uses bounds for the relevant body region. All probes, collision parameters, solver iterations and smoothing are preserved.

Offline tests used nine captured game frames and two tree initialization strategies. Mean breast–abdomen solve time fell from **12.81 ms to 1.66 ms**, approximately **87% less**. All 432 full solver comparisons and 44,648 nearest-surface comparisons matched 0.1.3.

These are offline solver timings on recorded game geometry, not in-game FPS measurements. They do not describe the performance of breast–body or breast–ground contact.

## Scope and limitations

The plugin reads the actual body mesh after pregnancy deformation and adds displacement only to breast roots. It does not change belly shape or add new belly or hair spring simulation. Extreme body shapes, clothing and poses may still show local clipping.

Diagnostic logging and the status overlay are disabled by default. Enable `CaptureEvidence` only when troubleshooting; snapshots are stored in a `diagnostics` folder beside the DLL.

See [Development and validation](DEVELOPMENT.md) for building, offline tests and replay tools. No game assets, third-party runtime DLLs, character meshes or captured scene data are distributed in this repository.
