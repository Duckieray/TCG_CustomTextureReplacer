# Custom Texture Replacer

Runtime mod for **TCG Card Shop Simulator** that lets you swap textures, meshes, and card metadata while the game is running. Drop files in, press a key if you want, and the game refreshes itself without a restart.

## Features

- **Live texture swapping** - watches every `CustomTextures` folder under `BepInEx/plugins/` (including sibling mods) and reloads PNGs automatically.
- **Persistent sprite overrides** - icons dropped into `CustomTextures` stay applied across atlas reloads and UI updates (store thumbnails, card icons, etc.).
- **Mesh and material overrides** - point `MeshOverrides.json` at Unity asset bundles to replace meshes, skinned meshes, colliders, and renderer materials at runtime.
- **Mesh-aware card metadata** - define `card` blocks inside `MeshOverrides.json` to rename store signage, checkout bars, price panels, and in-world props with new display names/aliases.
- **Shelf population controls** - optional `shelfCount` clamps how many instances of a mesh stay active per shelf root to stop overfilling figurine displays.
- **Hot reloads and watchers** - edits to textures, mesh overrides, or card metadata JSON are picked up immediately.
- **Configurable diagnostics** - optional debug logging, suppressed Unity spam, and manual dump triggers keep runtime overhead low but give you tools when you need them.

## Key Config Options (`BepInEx/config/com.duckieray.cardshop.customtextures.cfg`)

- `EnableAutomaticDumps` - when true, texture/sprite/mesh dumps regenerate on load; otherwise use the hotkeys.
- `EnableDebugLogFile` - toggle detailed logging to `CustomTextureReplacer.debug.log`.
- `SuppressRectTransformParentWarning` - hides Unity's noisy RectTransform parent warnings (enabled by default).
- `UseReplacementNames`, `LogNewTextureNames`, `LogAssetLoads`, and the mesh/card settings continue to behave as in previous releases.

## Installing or Updating

1. Copy `build/CustomTextureReplacer.dll` to `BepInEx/plugins/CustomTextureReplacer/` (or the equivalent r2modman profile directory).
2. Launch the game. Reference dumps such as `TexturesList.txt` and `MeshesList.txt` are only generated when you press the hotkeys or enable `EnableAutomaticDumps`.
3. Optional: set config values to control logging, dumps, or replacement name behaviour before you start playing.

> Tip: close the game before swapping DLLs; Windows otherwise keeps the previous binary locked in memory.

## Replacing Textures

1. Inspect `TexturesList.txt` (generate via F8 if needed) to find the texture name and resolution.
2. Drop a PNG with the exact texture name (case-insensitive) into any watched `CustomTextures` folder. Default: `BepInEx/plugins/CustomTextures/`.
3. The mod reloads the file automatically. A `CustomTextures.refresh.now` trigger or the F8 key remains available for bulk refreshes.

If `UseReplacementNames=true`, you can bake a new display name into the filename: `OriginalName-New_Display_Name.png`. Underscores become spaces when no other card metadata is present.

## Replacing Meshes

Mesh swapping uses Unity asset bundles (matching the game's Unity version) plus a `MeshOverrides.json` descriptor. Place the descriptor next to your textures or under `BepInEx/plugins/CustomTextureReplacer/`.

```json
{
  "overrides": [
    {
      "target": "PiggyA_Mesh",
      "bundle": "dark_knight_armored.bundle",
      "meshHint": "Armored",
      "materialHints": ["Body", "Head", "Pack"],
      "shelfCount": 3,
      "card": {
        "displayName": "Armored Batman Statue",
        "aliases": ["Pigni Plushie", "Pigni Plushie (12)"]
      }
    }
  ]
}
```

- `meshHint` narrows the auto-selected mesh name (case-insensitive).
- `materialHints` prioritises materials whose names contain each hint.
- `shelfCount` limits active duplicates under each shelf root; omit or set `-1` to keep the original layout.
- `card` is optional but recommended when you need UI labels to follow the new figurine name. Aliases cover in-game variations such as `Foo (6)`.

Newly spawned MeshFilter, SkinnedMeshRenderer, and MeshCollider components trigger the override scan immediately, so custom stock shows up on first appearance.

## Editing Card Metadata (global)

For cards that are not tied to a mesh override, create `CardOverrides.json`:

```json
{
  "entries": [
    {
      "Id": "PiggyA",
      "Aliases": ["Pigni"],
      "DisplayName": "Green Lantern",
      "CardNumber": "01",
      "Subtitle": "Emerald Guardian",
      "Description": "Wielder of willpower.\nDefends Sector 2814.",
      "EvolvesFrom": "Piglet",
      "Artist": "Hal Jordan",
      "Stat1": "150",
      "Stat2": "120",
      "Stat3": "90",
      "Stat4": "80",
      "Rarity": "Legendary",
      "Fame": "Justice League"
    }
  ]
}
```

### Supported Properties

| Key | Description |
|-----|-------------|
| `Id` | Monster ID from `TexturesList.txt` (example: `PiggyA`). |
| `Aliases` | Optional array of extra strings that should resolve to the same card. |
| `DisplayName` | Primary title shown on the card. |
| `CardNumber` | Top-left number slot (visible on templates that expose it). |
| `Subtitle` | Secondary line when the template supports it. |
| `Description` | Flavour text; use `\n` for manual line breaks. |
| `EvolvesFrom` | Populates the evolution label. |
| `Artist` | Artist credit badge. |
| `Stat1`-`Stat4` | Stat rows at the bottom; supply formatted strings. |
| `Rarity` | Rarity badge text. |
| `Fame` | Element/fame badge text. |

Search order: every discovered `*/CustomTextures/CardOverrides.json`, then `BepInEx/plugins/CustomTextureReplacer/CardOverrides.json`, then `BepInEx/plugins/CardOverrides.json`. The first match wins and is watched for changes.

## Built-in Dumps and Hotkeys

| Key | Action |
|-----|--------|
| F8  | Rebuilds `TexturesList.txt` and `SpritesList.txt`. |
| F9  | Writes `BepInEx/plugins/CardOverrides.original.json` using the captured card data. |
| F10 | Rebuilds `MeshesList.txt`. |

### Card data capture

While you browse the game, each rendered `CardUI` is captured and stored in memory. Press F9 after viewing cards to persist a snapshot. Log entries look like:

```
[time] Captured baseline card data for 'PiggyA'.
```

## Persistent Sprite / Icon Overrides

- Drop icon PNGs (for example `Icon_PiggyA.png`) into any watched `CustomTextures` folder.
- The plugin records the atlas region the first time it sees the icon, then reapplies that texture everywhere via Image/RawImage/Harmony hooks.
- Successful swaps log messages such as `UI Image override applied (hook): Icon_PiggyA -> Icon_PiggyA_Custom` when debug logging is enabled.

## Troubleshooting

| Symptom or log line | Resolution |
|---------------------|------------|
| "Could not watch card override file" | Check folder permissions; ensure the directory exists and is not read-only. |
| "Card override dump skipped - no card data captured yet" | Navigate through in-game card views before pressing F9 so the snapshot cache fills. |
| `CardOverrides entries were null after deserialisation` | JSON syntax error – validate the file (matching braces, commas, quotes, etc.). |
| Texture swap does nothing | File name mismatch or cached asset; create a `CustomTextures.refresh.now` trigger or press F8. |
| Mesh swap does nothing | Confirm the bundle uses the game's Unity version, the asset names match, and any texture folders referenced in the JSON exist; check the debug log if enabled. |
| UI label rename stutters | Ensure the mesh override `card.aliases` include every label variant (for example `Foo (6)`), so the cache can resolve it on the first attempt. |
| Need to see Unity RectTransform warnings | Set `SuppressRectTransformParentWarning=false` and restart; warnings are hidden by default to avoid log spam. |

## Building from Source

```
# build
dotnet build

# deploy (example r2modman profile)
copy build\CustomTextureReplacer.dll `
  "C:\Users\<you>\AppData\Roaming\r2modmanPlus-local\TCGCardShopSimulator\profiles\Default\BepInEx\plugins\Duckieray-CustomTextureReplacer\"
```

Ensure `Card Shop Simulator_Data/Managed` is available so the build references Unity assemblies correctly.

## Credits

- Plugin author: Duckieray
- Enhancements and maintenance: community contributors
- Powered by BepInEx 5 + Harmony 2
