<h1 align="center">Redout 2 Mod Manager</h1>

<p align="center">
  <a href="https://github.com/AlyraCreations/Redout-2-Mod-Manager/actions/workflows/dotnet-desktop.yml">
    <img alt="Build" src="https://github.com/AlyraCreations/Redout-2-Mod-Manager/actions/workflows/dotnet-desktop.yml/badge.svg?branch=main">
  </a>
  <a href="https://github.com/AlyraCreations/Redout-2-Mod-Manager/releases/latest">
    <img alt="Latest release" src="https://img.shields.io/github/v/release/AlyraCreations/Redout-2-Mod-Manager">
  </a>
</p>

The **Redout 2 Mod Manager** allows for the automation of installing and enabling/disabling of mods, as well as changing the loading priority and can identify conflicts when multiple mods are writing to the same address

## Requirements

- **Windows 10 or later, 64-bit**
- **.NET 10.0.4**

The app is **self-contained**

## First launch

Download `Redout2ModManager.exe` from the [latest release](https://github.com/AlyraCreations/Redout-2-Mod-Manager/releases/latest) on GitHub. After launching it, the app should **automatically detect** the Redout 2 game folder and the mods present. If it does not, click **Browse...** and select the game folder manually, which is located in: `C:\Program Files (x86)\Steam\steamapps\common\Redout2\redout2`.

To install mods using the mod manager, **drag .PAK files, or a ZIP archive onto the app**. ZIP archives can contain one or more mods.
You might have to drag some mods around before being able to apply the **automatic list ordering**.

## General functions

- **Checkboxes** indicate if a mod is **enabled** or **disabled**. Select **Confirm change(s)** to apply, or **Revert change(s)** to discard pending changes
- **Drag mods around** in the mod list to change their order, then confirm the change
- The mod manager will automatically detect **conflicting files**. Such files will be highlighted **yellow** and can be viewed by selecting **Inspect conflict(s)**. R2MM reads from PAK indexes when possible. For an unreadable or unsupported PAK index, manually include a `pak.list` to allow for conflict detection. Mods without a readable index are reported as **unchecked**
- **Uninstall selected** removes the selected files
- In **Options**, you can open the game's mod folder, toggle the wallpaper on/off, or choose a custom wallpaper **(very important lol)**

## Build

From the repository root, run:

```powershell
dotnet run -p R2MM.csproj
```

To create a release build:

```powershell
dotnet publish R2MM.csproj -c Release
```
