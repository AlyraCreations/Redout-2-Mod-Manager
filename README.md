<h1 align="center">Redout 2 Mod Manager</h1>

<p align="center">
  <a href="https://github.com/AlyraCreations/Redout-2-Mod-Manager/actions/workflows/dotnet-desktop.yml">
    <img alt="Build" src="https://github.com/AlyraCreations/Redout-2-Mod-Manager/actions/workflows/dotnet-desktop.yml/badge.svg?branch=main">
  </a>
  <a href="https://github.com/AlyraCreations/Redout-2-Mod-Manager/releases/latest">
    <img alt="Latest release" src="https://img.shields.io/github/v/release/AlyraCreations/Redout-2-Mod-Manager">
  </a>
  <a href="https://github.com/AlyraCreations/Redout-2-Mod-Manager/releases">
    <img alt="Latest pre-release" src="https://img.shields.io/github/v/release/AlyraCreations/Redout-2-Mod-Manager?include_prereleases&label=pre-release">
  </a>
</p>

The **Redout 2 Mod Manager** allows for the automation of installing and enabling/disabling of mods, as well as changing the loading priority and can identify conflicts when multiple mods are writing to the same address

## Requirements

- **Windows 10 or later, 64-bit**

Each release has two builds, so pick one:

- `Redout2ModManager.exe`: **includes .NET**, so there is nothing else to install (about 49 MB).
- `Redout2ModManager-NoRuntime.exe`: **small download** (under 1 MB), but it needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed.

## First launch

Download `Redout2ModManager.exe` from the [latest release](https://github.com/AlyraCreations/Redout-2-Mod-Manager/releases/latest) on GitHub. After launching it, the app should **automatically detect** the Redout 2 game folder and the mods present. If it does not, click **Browse...** and select the game folder manually, which is located in: `C:\Program Files (x86)\Steam\steamapps\common\Redout2\redout2`.

To install mods using the mod manager, **drag .PAK files, or a ZIP archive onto the app**. ZIP archives can contain one or more mods.
You might have to drag some mods around before being able to apply the **automatic list ordering**.

## General functions

- **Checkboxes** indicate if a mod is **enabled** or **disabled**. Select **Confirm change(s)** to apply, or **Revert change(s)** to discard pending changes.
- **Drag mods around** in the mod list to change their order, then confirm the change.
- The mod manager will automatically detect **conflicting files**. Such files will be highlighted **yellow** and can be viewed by selecting **Inspect conflict(s)**. R2MM reads from PAK indexes when possible. For an unreadable or unsupported PAK index, manually add a `<mod-name>.pak.list` file next to the PAK to allow for conflict detection. Mods without a readable index are reported as **unchecked**.
- **Uninstall selected** removes the selected files.
- In **Options**, you can open the game's mod folder, toggle the wallpaper on/off, or choose a custom wallpaper **(very important lol)**.

## Build

From the repository root, run:

```powershell
dotnet run --project R2MM.csproj
```

To create a release build:

```powershell
dotnet publish R2MM.csproj -c Release
```

## More info & support

Questions, feedback, bug reports or suggestions? Join the official Redout Discord and find us there.

<p align="center">
  <a href="https://discord.gg/redout">
    <img alt="Join the Redout Discord" src="https://img.shields.io/badge/Join%20the%20Redout%20Discord-5865F2?style=for-the-badge&logo=discord&logoColor=white">
  </a>
</p>

<h3 align="center">Developers</h3>

<p align="center">
  <a href="https://discord.com/users/492315815228473354">
    <img alt="Discord: AlyraCreations" src="https://img.shields.io/badge/AlyraCreations-5865F2?style=for-the-badge&logo=discord&logoColor=white&label=Discord">
  </a>
  <a href="https://discord.com/users/163315425818443776">
    <img alt="Discord: jaSnom" src="https://img.shields.io/badge/jaSnom-5865F2?style=for-the-badge&logo=discord&logoColor=white&label=Discord">
  </a>
</p>
