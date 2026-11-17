# Redout 2 Mod Manager

Redout 2 Mod Manager (R2MM) is a Windows desktop app for installing and organizing Redout 2 `.pak` mods. It can enable and disable mods, manage their load order, and identify files that multiple enabled mods provide.

## Requirements

- Windows 10 or later, 64-bit
- Redout 2 installed
- To build from source: the .NET 10 SDK

The published application is self-contained, so users do not need to install .NET separately.

## Getting started

Download and run `Redout2ModManager.exe` from a release. The app attempts to find Redout 2 in your Steam libraries. If it does not, choose **Browse...** and select the game folder, its `redout2` subfolder, `Content` folder, or `Paks` folder.

You can find mods through the app's **Links** menu, which includes Nexus Mods and GameBanana. To install a mod, drag its `.pak`, `.pak.disabled`, or ZIP archive onto the app. ZIP archives can contain one or more mods, and can include matching `.sig` signature files and `.pak.list` sidecar file lists.

## Managing mods

- Check or uncheck mods to stage enabling or disabling them. Select **Confirm change(s)** to apply, or **Revert change(s)** to discard pending changes.
- Drag mods in the list to stage a different load order, then confirm the change.
- Redout 2 loads PAK files in filename order. R2MM maintains the chosen order by renaming managed PAK files with numeric prefixes. Mods load from top to bottom; when mods provide the same file, the later mod overrides earlier ones.
- Use **Inspect conflict(s)** to search the detected conflicts. R2MM reads file lists from PAK indexes when possible. For an unreadable or unsupported PAK index, place a `<mod-name>.pak.list` file beside the PAK to enable conflict detection. Mods without a readable index or sidecar are reported as unchecked for conflicts.
- **Uninstall selected** removes the selected managed mod files, including optional signature and sidecar files. This deletes those files from disk.
- **Options** lets you open the game's Paks folder, toggle the wallpaper, or choose a custom wallpaper.

## Build from source

From the repository root, run:

```powershell
dotnet run --project R2MM.csproj
```

To create a release build:

```powershell
dotnet publish R2MM.csproj --configuration Release
```

The project targets `win-x64` and publishes a self-contained, single-file Windows application. The executable is written under `bin\Release\net10.0-windows\win-x64\publish\`.
