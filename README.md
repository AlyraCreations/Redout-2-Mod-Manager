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

To install mods using the mod manager, **drag .PAK files, or a ZIP, RAR or 7z archive onto the app**. Archives can contain one or more mods.
You might have to drag some mods around before being able to apply the **automatic list ordering**.

## General functions

- **Checkboxes** indicate if a mod is **enabled** or **disabled**. Select **Confirm change(s)** to apply, or **Revert change(s)** to discard pending changes
- **Drag mods around** in the mod list to change their order, then confirm the change
- The mod manager will automatically detect **conflicting files**. Such files will be highlighted **yellow** and can be viewed by selecting **View conflicts**. R2MM reads each mod's file index when possible. For an unreadable or unsupported index, manually include a `pak.list` to allow for conflict detection. Mods without a readable index are reported as **unchecked**
- **Uninstall selected** removes the selected files
- The **Options**, **Links** and **View conflicts** windows close when you click anywhere else in the app (or press Esc)
- In **Options**, you can open the game's mod folder, toggle the wallpaper on/off, or choose a custom wallpaper **(very important lol)**

## Mod browser

The **Mod browser** button in the sidebar opens a separate window that lists the Redout 2 mods on [Nexus Mods](https://www.nexusmods.com/games/redout2) with their thumbnail, title, creator, version, summary, number of downloads and endorsements, and the size of the main file. The button shows how many of your mods have an update. Closing the window only hides it, and pressing the **Mod browser** button again while it is open closes it.

- **Columns:** pick 1 to 7 columns of mods; the window widens if the chosen number does not fit, and the choice is remembered
- **Click a mod** to open its Nexus Mods page
- **Search**, sort, and filter by *Installed* or *Updates available*. The list is cached, so it opens instantly and refreshes itself when it is older than 30 minutes
- **Download / Update / Uninstall** buttons on every card. An update keeps the mod's load order position and its enabled/disabled state, and is rolled back if anything fails
- Browsing needs **no account**. Direct downloads need a personal **Nexus API key** (*Options > Nexus API key...*) **and a Premium account**, because Nexus only gives Premium members direct download links. On a free account (or without a key) the button opens the mod's Files tab: download the archive there and drop it onto R2MM (onto the main window or the browser window). Archives (ZIP, RAR or 7z) with the name Nexus generated are recognised, linked to their mod, and a newer one dropped later replaces the installed version in place
- On every launch (and whenever the mod list changes) R2MM scans for Nexus mods that were installed or removed outside the browser. Links to mods whose files are gone are dropped. Installed mods without a link (for example after deleting the link file in `%LOCALAPPDATA%`) are recognised when their file name matches a Nexus mod name (case, spaces, punctuation, the load-order number, versions and a trailing `_P` are ignored, and the match must be clear). They show as *Installed, version unknown*; **Reinstall** downloads the current version and links it so updates are tracked. Mod files hold no Nexus information, so a mod whose file name does not resemble its Nexus name cannot be recognised and needs to be installed again from the browser

## Build

From the repository root, run:

```powershell
dotnet run -p R2MM.csproj
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
