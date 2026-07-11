# MerchantStacker

QOL mod for Hollow Knight: Silksong — buy stackable merchant refills (rosary strings, shard pouches, etc.) in bulk instead of mashing confirm.

## Features

- On eligible **infinite-stock** shop items with a stack ceiling, confirm opens a **quantity submenu**
- **D-pad** up/down: ±1 (hold to accelerate)
- **Right stick** up/down: ±5 (hold to accelerate)
- Quantity is clamped by what you can afford and room until the inventory cap
- Works for **merchant shops** and **rosary stringing machines** (e.g. Greymoor)

## Install

1. Install [BepInExPack Silksong](https://thunderstore.io/c/hollow-knight-silksong/p/BepInEx/BepInExPack_Silksong/)
2. Drop `MerchantStacker.dll` into `BepInEx/plugins` (or install via Thunderstore / r2modman)
3. Configure in `BepInEx/config/io.github.raincloudthedragon.merchantstacker.cfg` (or F1 Configuration Manager)

## Build

```bash
dotnet new silksongpath --silksong-install-path "E:/SteamLibrary/steamapps/common/Hollow Knight Silksong"
dotnet build -c Release
```

Requires .NET SDK that can compile netstandard2.1 projects. Local `SilksongPath.props` is gitignored.
