# Custom Texture Replacer

Custom Texture Replacer lets you swap any in-game texture in **TCG Card Shop Simulator** at runtime. Drop your own PNG assets in the `BepInEx/plugins/CustomTextures` folder and the plugin will discover, resize if needed, and inject them without restarting the game. It is fully compatible with Thunderstore Mod Manager and r2modman.

## Features
- Watches `BepInEx/plugins/CustomTextures` for new or updated `.png` files and reapplies changes automatically.
- Maintains `BepInEx/plugins/TexturesList.txt`, a living index of every texture name and resolution discovered in the current session.
- Hooks into Unity asset loading so replacements stay applied across scene loads, asset bundle fetches, and dynamically created sprites.
- Falls back to automatic RenderTexture-based resizing when a replacement PNG does not match the original dimensions (matching resolutions still gives the best results).
- Ships with optional debug logging (`BepInEx/plugins/CustomTextureReplacer.debug.log`) for troubleshooting texture discovery and swap events.

## Requirements
- TCG Card Shop Simulator (latest Steam build).
- BepInEx 5.4+ (Mono) installed for the game.
- Windows has been tested; other OS targets should work so long as BepInEx 5 is available.

## Installation
### Thunderstore Mod Manager / r2modman
1. Open your manager, select **TCG Card Shop Simulator**, and browse the Thunderstore listing.
2. Locate **Custom Texture Replacer** and press **Download** (Latest).
3. Launch the profile; the plugin is enabled automatically on the next game start.

### Manual installation
1. Download the release zip and extract it into the game directory so that `CustomTextureReplacer.dll` ends up in `BepInEx/plugins/CustomTextureReplacer`.
2. Ensure the folder structure looks like:
   ```
   TCG Card Shop Simulator/
     BepInEx/
       plugins/
         CustomTextureReplacer/
           CustomTextureReplacer.dll
   ```
3. Start the game once to allow the plugin to create its working folders and configuration file.

## Usage
### Dump available texture names
- On first launch the plugin creates `BepInEx/plugins/TexturesList.txt`, containing every discovered texture name and resolution.
- Press `F8` in-game or create an empty `BepInEx/plugins/CustomTextures.dump.now` file to regenerate the list at any time.

### Replace textures
1. Open `BepInEx/plugins/TexturesList.txt` and locate the texture you want to override.
2. Create a PNG that matches the entry (the file name should be identical to the texture name; extension must be `.png`). Matching the resolution listed is strongly recommended.
3. Place the PNG in `BepInEx/plugins/CustomTextures`.
4. The plugin detects changes automatically and reapplies replacements on the fly. You can also force a refresh by creating `BepInEx/plugins/CustomTextures.refresh.now`.

> Tip: When the replacement PNG resolution differs, the plugin attempts a RenderTexture resize so the swap still succeeds. For best quality, export PNGs at the exact dimensions shown in `TexturesList.txt`.

### Debugging and logs
- Runtime status messages are written to `BepInEx/LogOutput.log` and to `BepInEx/plugins/CustomTextureReplacer.debug.log`.
- Enable verbose discovery output by toggling the debug settings described below.

## Configuration
Edit `BepInEx/config/com.duckieray.cardshop.customtextures.cfg` after the first run:

| Setting | Default | Description |
|---------|---------|-------------|
| `LogNewTextureNames` | `true` | Logs the names and resolutions of textures discovered during runtime scans. |
| `LogAssetLoads` | `true` | Logs when Unity `Resources` or `AssetBundle` APIs load textures or sprites, useful for tracking dynamic assets. |

Changes take effect immediately; you do not need to restart the game.

## Optional tools
- `CustomTextureReplacer/replace.py` is a cross-platform helper that batch-copies images from any folder into `CustomTextures`, renaming them to match entries in `TexturesList.txt`. It solves the repetitive work of matching filenames and supports both Windows and Linux installs.
  - Place your replacement art in a source folder and run `python replace.py <source_folder>` from inside `CustomTextureReplacer/`.
  - The script locates `TexturesList.txt`, filters textures by size (default `819x1024`), shuffles your images, and drops them into `BepInEx/plugins/CustomTextures` with the correct names.
  - Use `--size WIDTHxHEIGHT`, `--dump path/to/TexturesList.txt`, or `--output path/to/CustomTextures` to override defaults. Add `--seed <number>` for reproducible shuffles.
  - Running the script with no arguments prints a help summary that documents every option, making it easy to integrate into your art workflow.

## Troubleshooting
- **Nothing changes** - Double-check the PNG file name (case-insensitive) matches the entry in `TexturesList.txt` and that the file lives directly inside `BepInEx/plugins/CustomTextures`.
- **Blurred or stretched textures** - Export replacement art at the exact resolution reported in `TexturesList.txt` to avoid relying on the automatic resize.
- **Performance hiccups** - Large-scale swaps can be intensive the first time they apply. After the initial pass, only modified textures are reprocessed.
- **Still stuck?** - Inspect `BepInEx/plugins/CustomTextureReplacer.debug.log` and `BepInEx/LogOutput.log` for warnings; attach them when reporting issues.

## Changelog
### 1.3.0
- Initial Thunderstore release featuring automatic folder watching, texture dumps, runtime refresh triggers, and optional debug logging.

## Credits
- Plugin author: Duckieray
- Powered by BepInEx and Harmony
