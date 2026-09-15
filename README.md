# Build Resource Requirements

**Build Resources Requirements** is a mod that allows control over whether resources are required for specific build categories, tools, and pieces. This includes support for vanilla and modded categories, and configuration syncing. I wrote this mod because I didn't really like how the world modifier for disabling resources turned off everything, including the cultivator.

## Features
- **Category-Based Resource Requirements:** Configure resource requirements for specific build categories (e.g., Crafting, Furniture, Misc.).
- **Tool-Specific Configurations:** Enable or disable resource requirements for tools such as the Cultivator and Hoe.
- **Piece Exceptions:** Specify individual pieces that should never require resources, regardless of their category.
- **Modded Category Support:** Automatically detects and configures newly added modded categories.
- **Skill Based Resource Requirements:** Configurable option to reduce resource requirements depending on the players crafting skill level.
- **Multiplayer Synchronization:** Ensures all players share the same configuration when connected to a multiplayer server.

## Requirements
- Valheim **1.0.x** (Unity 6). Older game versions (0.22x and earlier) need mod version 1.1.0.
- **BepInExPack_Valheim 5.4.2350** or newer (BepInEx 5.4.23.5, Unity 6 aware).

## Installation
1. Install **BepInEx** (BepInExPack_Valheim 5.4.2350+).
2. Extract the `BuildResourcesMod.dll` into the `BepInEx/plugins` folder.
3. Launch Valheim to generate the configuration file.

## Building from source
The project is an SDK-style csproj and builds with the .NET 8 SDK (no Visual Studio needed):

```bash
dotnet build -c Release -p:GamePath="C:\Program Files (x86)\Steam\steamapps\common\Valheim"
```

`GamePath` defaults to the standard Steam location. `BepInExCore` defaults to `<GamePath>\BepInEx\core`
and can be overridden the same way. `lib/ServerSync.dll` is a build of
[blaxxun-boop/ServerSync](https://github.com/blaxxun-boop/ServerSync) compiled against Valheim 1.0.7 and is
merged into the output DLL by ILRepack, so the plugin ships as a single file.

## Configuration
The configuration file is generated in `BepInEx/config/Jammerbam.buildresourcesmod.cfg`.

### Categories
These are the available categories and their defaults:
```ini
[Categories]
MiscRequiresResources = true
CraftingRequiresResources = true
FurnitureRequiresResources = false
BuildingWorkbenchRequiresResources = false   (Build)
BuildingStonecutterRequiresResources = false (Heavy Building)
DeepNorthRequiresResources = false           (Deep North, new build tab in Valheim 1.0)
CultivatorRequiresResources = true
HoeRequiresResources = true
```

### Exceptions
Individual pieces can be added to the exceptions list:
```ini
[Exceptions]
## Comma-separated list of pieces that are always buildable.
# Setting type: String
# Default value: 
PieceExceptions = darkwood_wolf,darkwood_raven,wood_dragon1
```
Add piece names as a comma-separated list. Exceptions override category-based settings and never require resources.

### Skill Based Resource Reduction
The mod has an option to enable a feature to reduce resource requirements depending on the players crafting sill level.<br>
This works as a percentage, so if the player has a crafting skill of 50, the required resources will be 50% less.<br>
There is also another option to completly disable resource requirements if the player has a crafting skill of 100. If this option is disabled, all pieces will require at least 1 of each required item.

### Modded Categories
The mod will detect modded categories and add them to the config file. These categories are usually added when you load a world. If you want to configure a modded category, load a world, then close the game.<br>
Currently, the way that modded categories are displayed in the config file is by a numerical value, which is assigned when it is loaded in the game.

```ini
## Require resources for modded category: $category_9
# Setting type: Boolean
# Default value: true
9RequiresResources = true
```

If you need to find the category, enable the debugging option, and select a piece from the category. In the log it should show something like this:
```plaintext
[Info   : Unity Log] [BuildResourcesMod] Piece 'Armory_TW' in category '9' requires resources: True
```
You can also use this same method to find the name of a piece to add it to exceptions.

## Disclaimer
This is my very first mod, and my first time coding in C#. There are things that will inevitably be broken as I haven't been able to test for all scenarios. Please report if anything goes wrong so I can fix it.

## Changelog
### 1.2.0
- Updated for Valheim 1.0 (Unity 6). Rebuilt against the 1.0.7 game assemblies and BepInExPack_Valheim 5.4.2350.
- Bundled ServerSync rebuilt for 1.0 (the old build crashed on load with a `MissingFieldException`).
- Added the new **DeepNorth** build category to the config (defaults to not requiring resources, like the other building tabs).
- Modded category config descriptions now use the category label from the piece table instead of an unlocalized `$category_N` token.
- Fixed a null reference when a piece could not be resolved while checking requirements.
- Config version requirement raised to 1.2.0 so 1.1.0 clients are rejected by 1.2.0 servers (they would not work on Valheim 1.0 anyway).

## Planned Changes
- Fix the naming scheme in the config to make it more user-friendly.
- Continue updating and support for game updates.

## Credits
Almost all of the code was written by me however the config syncing is powered by:
https://github.com/blaxxun-boop/ServerSync
