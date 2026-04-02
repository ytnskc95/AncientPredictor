# AncientPredictor

A **Slay the Spire 2** mod that displays Ancient (Ancients/Elders) predictions directly as an in-game floating overlay on the map screen. No more switching to the browser!

## Features

- **In-game floating window** - Predictions appear as an overlay directly on the map screen
- **Collapsible** - Click the header to collapse/expand the panel, keeping your map view clean
- **Draggable** - Drag the header bar to reposition the window anywhere on screen
- **Map-only visibility** - The overlay automatically shows only when the map screen is open, and hides during combat, events, shops, etc.
- **All Ancients supported** - Orobas, Pael, Tezcatara, Nonupeipe, Tanx, Vakuu, Darv
- **Localized names** - Displays both localized relic names and internal IDs
- **Condition notes** - Shows relevant condition information (e.g., "Goopy >= 3", "Swift x4", "50% branch")
- **Non-gameplay-affecting** - Does not modify saves or mark runs as "modded"
- **Auto-refresh** - Predictions update when opening the map or entering a new act

## How It Works

The mod extracts the Ancient prediction algorithm from the game source code. It reconstructs the exact same RNG seed used by each Ancient event:

```
eventRng = new Rng((uint)(RunState.Rng.Seed + NetId + GetDeterministicHashCode(Id.Entry)))
```

Using this seed, it simulates the `GenerateInitialOptions()` logic for each Ancient type, predicting which 3 relics will be offered.

### Supported Ancients

| Ancient | Pool Logic |
|---------|-----------|
| **Orobas** | 3 pools with PrismaticGem/SeaGlass 33% split, conditional TouchOfOrobas/ArchaicTooth |
| **Pael** | 3 pools with Goopy/Removable conditions, list-doubling trick, pet check for PaelsLegion |
| **Tezcatara** | 3 simple pools (food/comfort/treasure) |
| **Nonupeipe** | 10-item pool with conditional BeautifulBracelet (Swift x4), shuffle & take 3 |
| **Tanx** | 10-item weapon pool with conditional TriBoomerang (Instinct x3), shuffle & take 3 |
| **Vakuu** | 3 pools each shuffled, pick first item |
| **Darv** | 9 relic sets (act-conditional), shuffle, 50% DustyTome branch |

### Important Notes

Predictions are based on the player state at the time of viewing (deck, relics, etc.). Since the actual Ancient options are generated when entering the Ancient room, conditional options (like ArchaicTooth, PaelsClaw) may differ if your deck/relics change before reaching the Ancient. However, the RNG seed and consumption sequence are deterministic.

## Installation

1. **Build** the project (requires Godot 4.5+ .NET SDK and Slay the Spire 2)
2. Copy `AncientPredictor.dll`, `AncientPredictor.json`, and `AncientPredictor.pck` to:
   ```
   <STS2 Install Dir>/mods/AncientPredictor/
   ```
3. Launch Slay the Spire 2 - the mod loads automatically

### Building from Source

1. Edit `AncientPredictor.csproj` and set `<Sts2Dir>` to your Slay the Spire 2 install path
2. Build with `dotnet build` or open in Visual Studio / Rider
3. The post-build script automatically copies files to the mods folder
4. Export `AncientPredictor.pck` from the Godot editor (Project > Export)

## Project Structure

```
AncientPredictor/
  AncientPredictor.csproj          # .NET project file
  AncientPredictor.json            # Mod manifest
  AncientPredictor.sln             # Solution file
  AncientPredictorMod.cs           # Mod entry point + Harmony patches
  project.godot                    # Godot project config
  export_presets.cfg               # Godot export settings
  FuturePredictor/
    AncientPredictor.cs            # Core prediction algorithm
  UI/
    AncientOverlay.cs              # Floating window overlay control
```

## Credits

- Prediction algorithm ported from [mcp-mod](https://github.com/ytnskc95/mcp-mod) by Luviagelita
- Game source reference from [STS2](https://github.com/ytnskc95/STS2) decompiled code

## License

This mod is provided as-is for the Slay the Spire 2 community. Not affiliated with MegaCrit.
