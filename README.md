# Custom Texture Replacer

Runtime mod for **TCG Card Shop Simulator** that lets you replace textures on the fly, customise card metadata, and export the original data set for reference. No restart required — drop files in, press a key, and the game refreshes itself.

## Features

- **Live texture swapping** – watches every `CustomTextures` folder under `BepInEx/plugins/` (including sibling mods) and reloads PNGs without restarting the game.
- **Live mesh and material swapping** – point `MeshOverrides.json` at Unity asset bundles to override meshes, skinned meshes, colliders, and renderer materials at runtime.
- **Mesh-specific texture support** – register extra texture folders for your meshes, or specify per-material texture files that the plugin loads and assigns at runtime.
- **Card metadata overrides** – optional `CardOverrides.json` lets you change card names, flavour text, stats, etc. per monster ID.
- **Hot reloads and watchers** – edits to textures, mesh overrides, or the card override JSON are picked up immediately.
- **Diagnostics** – writes activity to `CustomTextureReplacer.debug.log`, including capture events, alias mapping, and dump status.
- **Data export helpers** – F8 rebuilds the texture and sprite dumps; F9 emits a `CardOverrides.original.json` snapshot of the card data you have viewed.

## Installing / Updating

1. Copy `CustomTextureReplacer.dll` to `BepInEx/plugins/CustomTextureReplacer/` (or the r2modman profile equivalent `.../plugins/Duckieray-CustomTextureReplacer/`).
2. Ensure the companion `MiniJson.cs` is bundled into your build — `dotnet build` already produces the correct DLL.
3. Launch the game once to let the plugin create its working files:
   - `BepInEx/plugins/TexturesList.txt`
   - `BepInEx/plugins/SpritesList.txt`
   - `BepInEx/plugins/MeshesList.txt`
   - `BepInEx/plugins/CustomTextureReplacer.debug.log`

> **Tip:** Close the game before copying over the DLL. If the file is "locked by a user-mapped section", Windows still has the old binary loaded.

## Replacing Textures

1. Inspect `BepInEx/plugins/TexturesList.txt` to find the texture name and resolution.
2. Drop a PNG with the exact texture name (case-insensitive) into any watched `CustomTextures` folder. The default is `BepInEx/plugins/CustomTextures/`.
3. The mod automatically reloads the file. Use `CustomTextures.refresh.now` (empty file) or press F8 if you want to force a mass refresh.

PNG names support a rename suffix when the `UseReplacementNames` config flag is enabled:

```
OriginalName-New_Display_Name.png
```

The portion after the first hyphen becomes the fallback display name (underscores are turned into spaces) when no explicit card override is supplied.

## Replacing Meshes

Mesh swapping uses Unity asset bundles (built for the same Unity version as the game) plus a `MeshOverrides.json` descriptor. The file can live beside your textures in any watched `CustomTextures` folder, or directly under `BepInEx/plugins/CustomTextureReplacer/`.

```json
{
  "textureFolders": [
    "../Batman"
  ],
  "overrides": [
    {
      "target": "CounterMesh",
      "bundle": "Meshes/MyReplacement.bundle",
      "mesh": "CustomCounter",
      "applyToMeshFilters": true,
      "applyToSkinnedMeshRenderers": false,
      "applyToMeshColliders": true,
      "materials": [
        {
          "slot": 0,
          "material": "CounterMat",
          "bundle": "Meshes/MyReplacement.bundle",
          "textures": [
            {
              "property": "_BaseMap",
              "file": "../Batman/Textures/Counter_Diffuse.png",
              "wrap": "Repeat"
            }
          ]
        }
      ],
      "card": {
        "displayName": "Armored Counter",
        "aliases": ["CounterMesh"],
        "description": "Premium countertop imported from Gotham."
      }
    }
  ]
}
```

- `textureFolders` registers additional directories (relative to the JSON) that should be scanned exactly like the regular `CustomTextures` folders. Drop PNGs there and they will auto-reload and participate in the global texture pipeline.
- `target` matches the original mesh name listed in `MeshesList.txt`.
- `bundle` is resolved relative to the JSON file (or any watched `CustomTextures` folder / plugin root). Supply the full file name of the asset bundle you exported.
- `mesh` is the asset name inside the bundle. The loader instantiates it and reuses it for all matching components. Leave it blank to let the loader auto-select the most likely mesh (see below).
- `applyToMeshFilters`, `applyToSkinnedMeshRenderers`, and `applyToMeshColliders` control where the override runs. They default to `true`, `true`, and `false` respectively.
- `materials` is optional. Each entry replaces a renderer material slot (`slot` index) with a material loaded from either the same bundle or a per-entry `bundle`. If you omit `materials`, the loader auto-selects the first materials inside the bundle (guided by optional `materialHints`).
- `textures` (optional) lets you point a material property at a PNG on disk. The plugin loads the texture, applies wrap/filter/aniso overrides, and assigns it to the instantiated material - perfect for meshes that expect new texture assets outside of the bundle.
- `card` (optional) mirrors `CardOverrides.json`. Supply `displayName`, `aliases`, `description`, etc., and the store/price UI will adopt those values whenever that mesh is shown.

The plugin watches the JSON file, every referenced bundle, and any registered texture folders. Updating any of them schedules a reload. Press F10 or create `MeshesList.dump.now` to rebuild `MeshesList.txt` when you need to confirm original names.

### Auto-selection quick start

Want to skip listing mesh/material names? Just point at the bundle:

```json
{
  "overrides": [
    {
      "target": "PiggyA_Mesh",
      "bundle": "dark_knight_armored.bundle"
    }
  ]
}
```

The loader picks the most complex mesh in the bundle and assigns enough materials to cover its sub-mesh count. Add optional hints when a bundle contains multiple candidates:

```json
{
  "overrides": [
    {
      "target": "PiggyA_Mesh",
      "bundle": "dark_knight_armored.bundle",
      "meshHint": "Armored",
      "materialHints": ["Body", "Head", "Pack"],
      "card": {
        "displayName": "Armored Batman Statue",
        "aliases": ["Pigni Plushie"]
      }
    }
  ]
}
```

`meshHint` narrows the auto mesh selection by name (case-insensitive). `materialHints` prioritises materials whose names contain each hint. If the loader still cannot determine a unique mesh or enough materials, it logs a warning so you can fall back to explicit names.
## Editing Card Metadata

Create a `CardOverrides.json` with the following structure:

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
      "Stat1": "STR: 150",
      "Stat2": "MAG: 120",
      "Stat3": "SPR: 90",
      "Stat4": "SPD: 80",
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
| `Aliases` | Optional array of extra strings that should match the same card. |
| `DisplayName` | Primary title shown on the card. |
| `CardNumber` | Top-left number slot (visible on templates that expose it). |
| `Subtitle` | Secondary line (Champion badge) when the template supports it. |
| `Description` | Flavour text; use `\n` for manual line breaks. |
| `EvolvesFrom` | Populates the evolution label. |
| `Artist` | Artist credit badge. |
| `Stat1`-`Stat4` | Stat rows at the bottom; supply your own formatted strings. |
| `Rarity` | Rarity badge text. |
| `Fame` | Element/fame badge text. |

Place `CardOverrides.json` in any watched `CustomTextures` directory. The loader searches in this order:

1. Every discovered `.../CustomTextures/CardOverrides.json`
2. `BepInEx/plugins/CustomTextureReplacer/CardOverrides.json`
3. `BepInEx/plugins/CardOverrides.json`

The first file found wins, and the watcher keeps it in sync with on-disk edits.

## Built-in Dumps and Key Bindings

| Key | Action |
|-----|--------|
| F8  | Rebuilds `TexturesList.txt` and `SpritesList.txt`. |
| F9  | Writes `BepInEx/plugins/CardOverrides.original.json` using the captured card data. |
| F10 | Rebuilds `MeshesList.txt`. |

### Card data capture

While you browse the game, each rendered `CardUI` is captured and stored in an in-memory snapshot. The F9 dump serialises that snapshot. If you press F9 before visiting any cards, the dump will be empty — open the album or packs first to populate the cache.

Each capture is logged in `CustomTextureReplacer.debug.log` as:

```
[time] Captured baseline card data for 'PiggyA'.
```

## Debugging and Logs

- `BepInEx/plugins/CustomTextureReplacer.debug.log` keeps a detailed trace: texture discoveries, mesh reloads, override hits, card capture events, alias mappings, and dump status.
- Use the log to confirm overrides are loading:
  - `CardOverrides JSON length: ...`
  - `CardOverrides added entry for ...`
  - `MeshOverrides load complete: ...`

## Troubleshooting

| Symptom or log line | Resolution |
|---------------------|------------|
| "Could not watch card override file" | Check folder permissions; ensure the directory exists and is not read-only. |
| "Card override dump skipped - no card data captured yet" | Navigate through the in-game card views before pressing F9 so the snapshot cache is filled. |
| `CardOverrides entries were null after deserialisation` | JSON syntax problem — validate the file (no trailing commas, matching braces, etc.). |
| Texture swap does nothing | File name mismatch or cached asset; drop a `CustomTextures.refresh.now` trigger or press F8. |
| Mesh swap does nothing | Confirm `MeshOverrides.json` resolved the correct bundle and asset names, the bundle uses the game's Unity version, and that any external texture folders/files are spelled correctly; check `CustomTextureReplacer.debug.log` for warnings. |

## Building from Source

```
# build
cd CustomTextureReplacer
 dotnet build

# deploy (example r2modman profile)
copy build\CustomTextureReplacer.dll "C:\Users\<you>\AppData\Roaming\r2modmanPlus-local\TCGCardShopSimulator\profiles\Default\BepInEx\plugins\Duckieray-CustomTextureReplacer\"
```

Ensure `Card Shop Simulator_Data/Managed` is accessible so the build references Unity assemblies correctly.

## Credits

- Plugin author: Duckieray
- Enhancements and maintenance: community contributors
- Powered by BepInEx 5 + Harmony 2


