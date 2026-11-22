using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using Microsoft.Win32;
using SharpCompress.Archives;

namespace R2MM;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

// The main application: game folder, manifest, import/install, enable/disable, uninstall, the mod list, conflict detection, load order and PAK discovery.
internal sealed partial class MainForm : Form
{
    private const string SteamAppId = "1799930";
    private const string SteamLaunchUri = "steam://rungameid/" + SteamAppId;
    private const string DiscordUrl = "https://discord.gg/redout";
    private const string NexusUrl = "https://www.nexusmods.com/redout2";
    private const string GameBananaUrl = "https://gamebanana.com/games/24088";
    private const string ModOrderDragFormat = "Redout2ModManager.ModOrder";
    private const string StagingSuffix = ".reorder.tmp";

    private static readonly JsonSerializerOptions JsonIndented = new() { WriteIndented = true };
    private static readonly string SettingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "R2MM");
    private static readonly string SettingsFile = Path.Combine(SettingsDirectory, "settings.json");
    private static readonly string WindowFile = Path.Combine(SettingsDirectory, "window.json");
    private static readonly string[] ModStateLabels = ["Missing / conflict", "Enabled", "Disabled", "Disabled (legacy)", "Missing / conflict"];

    private readonly TextBox _gameDirectoryBox = new() { ReadOnly = true };
    private readonly TextBox _modSearchBox = new();
    private readonly BufferedListView _modList = new();
    private readonly Button _changesConfirmButton, _changesRevertButton, _modUninstallButton, _conflictViewButton;
    private readonly Label _consoleStatus = new() { ForeColor = Theme.MutedColor, Location = new Point(72, 4), Size = new Size(650, 18), AutoEllipsis = true, Cursor = Cursors.Hand };
    private readonly TextBox _consoleHistory = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
        BackColor = Theme.PanelColor, ForeColor = Theme.MutedColor, Font = Theme.CreateFont(8), Visible = false
    };
    private Action _fillModListColumn; // set in the constructor by Theme.ConstrainColumns
    private readonly System.Windows.Forms.Timer _modListResizeTimer = new() { Interval = 40 };

    private readonly Dictionary<string, bool> _modDesiredStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModFileSet> _modFileSets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Stamp, ModFileSet FileSet)> _modFileSetCache = new(StringComparer.OrdinalIgnoreCase);
    private List<ModEntry> _managedMods = [];
    private List<ConflictRow> _conflictRows = [];
    private List<string> _conflictUnreadableMods = [];

    private string _gameDirectory = "", _gameDirectoryPaks = "", _manifestFile = "", _legacyDisabledDirectory = "", _wallpaperCustomPath = "";
    private Bitmap? _wallpaperImage;
    private bool _modListUpdating, _windowResizing, _wallpaperEnabled = true;
    private int _importCleanupFailures;

    private enum ModState { Missing, Enabled, Disabled, DisabledLegacy, Conflict }

    private sealed class ModEntry
    {
        public string FileName { get; set; } = "";
        public bool HasSignature { get; set; }
        public string DisplayName => Path.GetFileNameWithoutExtension(FileName); // serialized for manifest compatibility, ignored on load
    }

    private sealed class ManagerSettings
    {
        public string GameDirectory { get; set; } = "";
        public string WallpaperPath { get; set; } = "";
        public string NexusApiKey { get; set; } = "";
        public bool NexusKeyPromptShown { get; set; }
        public int BrowserColumns { get; set; } = 1;
    }

    private sealed class ImportTally
    {
        public int Imported, AlreadyManaged;
        public readonly List<string> FileNames = []; // PAK names that were installed, in install order
        public readonly Dictionary<string, bool> RestoredStates = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A PAK being imported. Open(name) returns a copy action for the PAK or one of its siblings (.sig, .list), or null when absent.</summary>
    private sealed record PakSource(string PakName, Func<string, Action<string>?> Open, Action<string>? Remove = null);

    // ---- Paths and file-name helpers

    private string PakPath(string fileName) => Path.Combine(_gameDirectoryPaks, fileName);
    private string DisabledPath(string fileName) => PakPath(fileName + ".disabled");
    private string LegacyDisabledPath(string fileName) => Path.Combine(_legacyDisabledDirectory, fileName);
    private Func<string, string>[] ModLocations => [PakPath, DisabledPath, LegacyDisabledPath];
    private string[] ModPaths(string fileName) => [.. ModLocations.Select(location => location(fileName))];
    private bool SignatureExists(string pakFileName) => ModLocations.Take(2).Any(location => File.Exists(location(SigName(pakFileName))));

    private static bool IsDisabledPakPath(string path) => path.EndsWith(".pak.disabled", StringComparison.OrdinalIgnoreCase);
    private static bool IsPakImportPath(string path) => path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || IsDisabledPakPath(path);
    private static bool IsSupportedImportPath(string path) => ModArchive.IsSupported(path) || IsPakImportPath(path);
    private static bool IsSafePakFileName(string fileName) =>
        fileName != "" && Path.GetFileName(fileName) == fileName && fileName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);

    private static string GetPakFileName(string path)
    {
        var fileName = Path.GetFileName(path);
        return IsDisabledPakPath(fileName) ? fileName[..^".disabled".Length] : fileName;
    }

    private static string SigName(string pakFileName) => Path.ChangeExtension(pakFileName, ".sig");
    private static string[] ModNames(ModEntry mod) => mod.HasSignature ? [mod.FileName, SigName(mod.FileName)] : [mod.FileName];
    private IEnumerable<string> AllPaths(ModEntry mod) => ModNames(mod).SelectMany(name => ModPaths(name)).Append(PakPath(mod.FileName + ".list"));

    private ModEntry? FindMod(string fileName) => _managedMods.FirstOrDefault(mod => mod.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));

    private ModState GetModState(string fileName)
    {
        var found = ModPaths(fileName).Select(File.Exists).ToArray();
        return found.Count(isFound => isFound) > 1 ? ModState.Conflict : (ModState)(Array.IndexOf(found, true) + 1);
    }

    private void SortModsByFileName() => _managedMods.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));

    // ---- Drag and drop of files from Explorer

    private void EnableFileDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += HandleFileDragEnter;
        control.DragDrop += HandleFileDrop;
        foreach (Control child in control.Controls) EnableFileDrop(child);
    }

    private void HandleFileDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = DragDropEffects.None;
        if (e.Data is null) return;
        if (e.Data.GetDataPresent(ModOrderDragFormat)) { if (sender == _modList) e.Effect = DragDropEffects.Move; }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Any(IsSupportedImportPath)) e.Effect = DragDropEffects.Copy;
    }

    private void HandleFileDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && EnsureGameSelected())
            ImportModFiles(paths.OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToList());
    }

    // ---- Startup, game folder, settings

    private void InitializeManager()
    {
        try
        {
            var settings = File.Exists(SettingsFile) ? JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(SettingsFile)) : null;
            var savedGameDirectory = settings?.GameDirectory ?? "";
            var savedWallpaper = settings?.WallpaperPath ?? "";
            _nexusApiKey = _nexus.ApiKey = settings?.NexusApiKey ?? "";
            _nexusKeyPromptShown = settings?.NexusKeyPromptShown ?? false;
            if (File.Exists(savedWallpaper))
            {
                _wallpaperCustomPath = savedWallpaper;
                _wallpaperImage?.Dispose();
                _wallpaperImage = LoadWallpaper(savedWallpaper);
            }

            var gameDirectory = FindPaksDirectory(savedGameDirectory) is null ? FindSteamGameDirectory() : savedGameDirectory;
            if (gameDirectory != "") SetGameDirectory(gameDirectory, saveSettings: false);
            else
            {
                _gameDirectoryBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Redout2");
                SetStatus("Could not find Redout 2 automatically. Choose the game folder.");
            }
        }
        catch (Exception ex) { ShowError("Could not initialize the manager.", ex); }
    }

    private void BrowseForGameDirectory()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select the Redout 2 installation folder", UseDescriptionForTitle = true, SelectedPath = _gameDirectory };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { SetGameDirectory(dialog.SelectedPath, saveSettings: true); }
        catch (Exception ex) { ShowError("Could not use that game folder.", ex); }
    }

    private void SetGameDirectory(string gameDirectory, bool saveSettings)
    {
        _gameDirectoryPaks = FindPaksDirectory(gameDirectory) ?? throw new DirectoryNotFoundException("Could not find redout2/Content/Paks inside the selected folder.");
        _gameDirectory = Path.GetFullPath(gameDirectory);
        _gameDirectoryBox.Text = _gameDirectory;
        _modDesiredStates.Clear();

        var installKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(_gameDirectoryPaks).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..16];
        _manifestFile = Path.Combine(SettingsDirectory, $"{installKey}-mods.json");
        _legacyDisabledDirectory = Path.Combine(SettingsDirectory, "DisabledMods", installKey);
        _nexusLinksFile = Path.Combine(SettingsDirectory, $"{installKey}-nexus.json");
        LoadNexusLinks();

        LoadManifest();
        DiscoverInstalledMods();
        RefreshModList();
        if (saveSettings) SaveSettings();
    }

    private bool EnsureGameSelected()
    {
        if (Directory.Exists(_gameDirectoryPaks)) return true;
        MessageBox.Show(DialogParent, "Select your Redout 2 installation folder first.", "Game folder required", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private void SaveSettings() => SaveJson(SettingsFile, new ManagerSettings
    {
        GameDirectory = _gameDirectory, WallpaperPath = _wallpaperCustomPath, NexusApiKey = _nexusApiKey, NexusKeyPromptShown = _nexusKeyPromptShown, BrowserColumns = _modBrowser.Columns
    });

    private void SaveManifest() => SaveJson(_manifestFile, _managedMods);

    private static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonIndented));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private void LoadManifest()
    {
        _managedMods.Clear();
        if (!File.Exists(_manifestFile)) return;
        try
        {
            foreach (var entry in JsonSerializer.Deserialize<List<ModEntry>>(File.ReadAllText(_manifestFile)) ?? throw new InvalidDataException("The manifest is empty."))
            {
                if (!IsSafePakFileName(entry.FileName)) throw new InvalidDataException($"Invalid file name in the mod manifest: {entry.FileName}");
                if (FindMod(entry.FileName) is not null) throw new InvalidDataException($"Duplicate file name in the mod manifest: {entry.FileName}");
                _managedMods.Add(entry);
            }
            SortModsByFileName();
        }
        catch (Exception ex)
        {
            _managedMods.Clear();
            throw new InvalidDataException($"The mod manifest could not be loaded ({_manifestFile}): {ex.Message}", ex);
        }
    }

    // ---- Import (loose .pak / .pak.disabled files and ZIP, RAR or 7z archives)

    private void ImportModFiles(IEnumerable<string> paths)
    {
        _importCleanupFailures = 0;
        var orderWasPending = HasPendingOrder();
        var tally = new ImportTally();
        var failures = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (ModArchive.IsSupported(path)) ImportNexusAwareArchive(path, tally);
                else if (IsPakImportPath(path)) ImportLoosePak(path, tally);
                else throw new InvalidDataException("Choose a .pak, .pak.disabled, or a ZIP, RAR or 7z archive containing .pak or .pak.disabled mods.");
            }
            catch (Exception ex) { failures.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }

        if (!orderWasPending)
        {
            SortModsByFileName();
            try { SaveManifest(); } catch { }
        }
        RefreshModList(preserveDesiredStates: true, stateOverrides: tally.RestoredStates);

        if (failures.Count == 0)
        {
            SetStatus((tally.Imported == 0 ? $"No new mods imported; already installed: {tally.AlreadyManaged}." : $"Imported or restored {tally.Imported} mod(s); already installed: {tally.AlreadyManaged}.")
                + (_importCleanupFailures > 0 ? $" Could not remove {_importCleanupFailures} original file(s) after copying them." : ""));
            return;
        }
        SetStatus($"Imported or restored {tally.Imported} mod(s); {failures.Count} failed.");
        MessageBox.Show(this, $"Imported or restored {tally.Imported} mod(s).\r\n\r\nCould not import:\r\n{string.Join("\r\n", failures)}", "Mod import results", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void ImportArchive(string archivePath, ImportTally tally)
    {
        using var archive = ModArchive.Open(archivePath);
        var sources = archive.EntryPaths
            .Where(IsPakImportPath)
            .Select(pakEntryPath => new PakSource(Path.GetFileName(pakEntryPath),
                siblingName => archive.CopyEntry(pakEntryPath[..(pakEntryPath.LastIndexOf('/') + 1)] + siblingName)))
            .ToList();
        if (sources.Count == 0) throw new InvalidDataException("The archive does not contain a .pak or .pak.disabled mod file.");
        InstallPaks(sources, tally);
    }

    private void ImportLoosePak(string path, ImportTally tally)
    {
        var fullPath = Path.GetFullPath(path);
        var sourceDirectory = Path.GetDirectoryName(fullPath)!;
        var fileName = GetPakFileName(fullPath);
        if (sourceDirectory.Equals(_gameDirectoryPaks, StringComparison.OrdinalIgnoreCase))
        {
            // Already inside the game's Paks folder: register it in place instead of copying.
            if (FindMod(fileName) is not null) tally.AlreadyManaged++;
            else
            {
                RegisterExistingPak(fileName, IsDisabledPakPath(fullPath));
                tally.Imported++;
            }
            return;
        }
        // Loose files are moved: the originals are deleted once everything was copied and saved.
        InstallPaks([new PakSource(Path.GetFileName(fullPath),
            siblingName => File.Exists(Path.Combine(sourceDirectory, siblingName)) ? target => File.Copy(Path.Combine(sourceDirectory, siblingName), target) : null,
            siblingName => RemoveMovedSource(Path.Combine(sourceDirectory, siblingName)))], tally);
    }

    private void RemoveMovedSource(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch { _importCleanupFailures++; }
    }

    private void RegisterExistingPak(string fileName, bool isDisabled)
    {
        if (!IsSafePakFileName(fileName)) throw new InvalidDataException($"Invalid mod file name: {fileName}");
        if (GetModState(fileName) is ModState.Missing or ModState.Conflict) throw new IOException($"'{fileName}' is missing or has another enabled or disabled copy.");
        var signature = SigName(fileName);
        var mod = new ModEntry { FileName = fileName, HasSignature = File.Exists(isDisabled ? DisabledPath(signature) : PakPath(signature)) };
        _managedMods.Add(mod);
        try { SaveManifest(); }
        catch
        {
            _managedMods.Remove(mod);
            throw;
        }
    }

    /// <summary>Validates every source first, then copies all files (rolling back on failure) and updates the manifest.</summary>
    private void InstallPaks(IEnumerable<PakSource> sources, ImportTally tally)
    {
        var copies = new List<(Action<string> Copy, string Target, Action? RemoveSource)>();
        var newMods = new List<ModEntry>();
        var restoredMods = new List<(ModEntry Mod, bool HadSignature, bool HasSignature, bool Enabled)>();
        var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var alreadyManaged = 0;

        foreach (var source in sources)
        {
            var sourceName = GetPakFileName(source.PakName);
            var isDisabled = IsDisabledPakPath(source.PakName);
            if (!IsSafePakFileName(sourceName)) throw new InvalidDataException($"Invalid mod file name: {sourceName}");
            if (!seenFileNames.Add(sourceName)) throw new InvalidDataException($"The import contains multiple mod files with the same name: {sourceName}");

            var managedMod = FindMod(sourceName);
            if (managedMod is null)
            {
                if (File.Exists(PakPath(sourceName))) throw new IOException($"'{sourceName}' already exists in the game's mod folder. Import that file to register it without replacing it.");
                if (File.Exists(DisabledPath(sourceName)) || File.Exists(LegacyDisabledPath(sourceName))) throw new IOException($"A disabled mod with this name is already stored: {sourceName}");
            }
            else
            {
                var state = GetModState(managedMod.FileName);
                if (state == ModState.Conflict) throw new IOException($"'{managedMod.FileName}' has conflicting enabled and disabled copies. Resolve the conflict before importing it.");
                if (state != ModState.Missing)
                {
                    alreadyManaged++;
                    continue;
                }
            }

            var fileName = managedMod?.FileName ?? sourceName;
            var sourceSignature = SigName(sourceName) + (isDisabled ? ".disabled" : "");
            var targetSignature = SigName(fileName);
            var hasSignature = ValidateSignature(fileName, isDisabled, source.Open(sourceSignature) is not null, managedMod?.HasSignature);
            string TargetPath(string name) => isDisabled ? DisabledPath(name) : PakPath(name);
            void Queue(string siblingName, string target)
            {
                if (source.Open(siblingName) is { } copy) copies.Add((copy, target, () => source.Remove?.Invoke(siblingName)));
            }

            Queue(source.PakName, TargetPath(fileName));
            Queue(sourceSignature, TargetPath(targetSignature));
            if (!File.Exists(PakPath(fileName + ".list"))) Queue(sourceName + ".list", PakPath(fileName + ".list"));

            if (managedMod is null) newMods.Add(new ModEntry { FileName = fileName, HasSignature = hasSignature });
            else restoredMods.Add((managedMod, managedMod.HasSignature, hasSignature, !isDisabled));
        }

        var createdFiles = new List<string>();
        try
        {
            foreach (var (copy, target, _) in copies)
            {
                copy(target);
                createdFiles.Add(target);
            }
            _managedMods.AddRange(newMods);
            foreach (var restored in restoredMods) restored.Mod.HasSignature = restored.HasSignature;
            if (copies.Count > 0) SaveManifest();
        }
        catch
        {
            foreach (var mod in newMods) _managedMods.Remove(mod);
            foreach (var restored in restoredMods) restored.Mod.HasSignature = restored.HadSignature;
            foreach (var path in createdFiles) DeleteIfExists(path);
            throw;
        }

        foreach (var (_, _, removeSource) in copies) removeSource?.Invoke();
        tally.Imported += newMods.Count + restoredMods.Count;
        tally.FileNames.AddRange(newMods.Select(mod => mod.FileName));
        tally.FileNames.AddRange(restoredMods.Select(restored => restored.Mod.FileName));
        tally.AlreadyManaged += alreadyManaged;
        foreach (var restored in restoredMods) tally.RestoredStates[restored.Mod.FileName] = restored.Enabled;
    }

    /// <summary>Checks the .sig files on disk against the import and returns whether the mod owns a signature afterwards.
    /// managedHasSignature is null for a brand-new mod.</summary>
    private bool ValidateSignature(string pakFileName, bool isDisabled, bool sourceHasSignature, bool? managedHasSignature)
    {
        if (managedHasSignature is null && !sourceHasSignature) return false;
        var signature = SigName(pakFileName);
        var target = isDisabled ? DisabledPath(signature) : PakPath(signature);
        var opposite = isDisabled ? PakPath(signature) : DisabledPath(signature);
        var targetExists = File.Exists(target);
        if (File.Exists(opposite)) throw new IOException($"A signature file in the opposite enabled state already exists: {Path.GetFileName(opposite)}");
        if (targetExists && (sourceHasSignature || managedHasSignature != true)) throw new IOException($"A signature file already exists: {Path.GetFileName(target)}");
        if (managedHasSignature == true && !sourceHasSignature && !targetExists) throw new FileNotFoundException("The installed signature file is missing and the import does not provide one.", target);
        return sourceHasSignature || targetExists;
    }

    // ---- Enable / disable, uninstall

    private int ApplyModChanges()
    {
        var desiredStates = _modList.Items.Cast<ListViewItem>().ToDictionary(item => (string)item.Tag!, item => item.Checked, StringComparer.OrdinalIgnoreCase);
        var changedCount = 0;
        try
        {
            foreach (var mod in _managedMods)
            {
                var state = GetModState(mod.FileName);
                if (state == ModState.Missing) continue;
                if (state == ModState.Conflict) throw new IOException($"Cannot determine the state of '{mod.FileName}'. Its enabled and disabled copies are conflicting or missing.");
                if (!desiredStates.TryGetValue(mod.FileName, out var shouldBeEnabled) || shouldBeEnabled == (state == ModState.Enabled)) continue;

                MoveFiles(ModNames(mod).Select(name => shouldBeEnabled
                    ? (state == ModState.DisabledLegacy ? LegacyDisabledPath(name) : DisabledPath(name), PakPath(name))
                    : (PakPath(name), DisabledPath(name))).ToList());
                changedCount++;
            }
            RefreshModList();
            return changedCount;
        }
        catch (Exception ex)
        {
            RefreshModList(preserveDesiredStates: true);
            ShowError("Could not apply all mod changes.", ex);
            return -1;
        }
    }

    /// <summary>Moves every file through a temporary name (so names can be swapped) and undoes all moves when any step fails.
    /// afterMoves runs once everything is moved and counts as part of the transaction.</summary>
    private static void MoveFiles(IReadOnlyList<(string Source, string Destination)> moves, Action? afterMoves = null)
    {
        var sources = moves.Select(move => move.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, destination) in moves)
        {
            if (!File.Exists(source)) throw new FileNotFoundException("An installed mod file is missing.", source);
            if (!destinations.Add(destination)) throw new IOException($"Two files would be renamed to {Path.GetFileName(destination)}.");
            if (File.Exists(destination) && !sources.Contains(destination)) throw new IOException($"Refusing to overwrite an existing file: {Path.GetFileName(destination)}");
            if (File.Exists(source + StagingSuffix)) throw new IOException($"A leftover temporary file exists: {Path.GetFileName(source)}{StagingSuffix}");
        }

        var undo = new Stack<(string Current, string Previous)>();
        try
        {
            foreach (var (source, _) in moves)
            {
                MoveFile(source, source + StagingSuffix);
                undo.Push((source + StagingSuffix, source));
            }
            foreach (var (source, destination) in moves)
            {
                MoveFile(source + StagingSuffix, destination);
                undo.Push((destination, source + StagingSuffix));
            }
            afterMoves?.Invoke();
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var (current, previous) in undo)
            {
                try { MoveFile(current, previous); }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (rollbackErrors.Count > 0) throw new AggregateException("The rename failed and rollback was incomplete.", rollbackErrors.Prepend(error));
            throw;
        }
    }

    /// <summary>File.Move that also works between drives (copy, then delete the source).</summary>
    private static void MoveFile(string source, string destination)
    {
        if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            File.Move(source, destination);
            return;
        }
        File.Copy(source, destination);
        try { File.Delete(source); }
        catch
        {
            File.Delete(destination);
            throw;
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private void UninstallSelectedMods()
    {
        var selectedNames = _modList.SelectedItems.Cast<ListViewItem>().Select(item => (string)item.Tag!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedMods = _managedMods.Where(mod => selectedNames.Contains(mod.FileName)).ToList();
        if (selectedMods.Count > 0) UninstallMods(selectedMods, $"Uninstall {selectedMods.Count} selected mod(s)? Their mod files and any signature files will be deleted.");
    }

    /// <summary>Asks for confirmation, then deletes the mods' files. Returns true when they were uninstalled.</summary>
    private bool UninstallMods(IReadOnlyList<ModEntry> selectedMods, string confirmation)
    {
        if (MessageBox.Show(this, confirmation, "Confirm uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
        try
        {
            foreach (var mod in selectedMods)
            {
                foreach (var path in AllPaths(mod)) DeleteIfExists(path);
                _managedMods.Remove(mod);
                SaveManifest();
            }
            RefreshModList(preserveDesiredStates: true);
            SetStatus($"Uninstalled {selectedMods.Count} mod(s).");
            return true;
        }
        catch (Exception ex)
        {
            RefreshModList(preserveDesiredStates: true);
            ShowError("Could not uninstall all selected mods.", ex);
            return false;
        }
    }

    // ---- Mod list

    private void RefreshMods()
    {
        if (!EnsureGameSelected()) return;
        var previousMods = _managedMods.ToList();
        try
        {
            LoadManifest();
            var discoveredCount = DiscoverInstalledMods();
            var removedCount = _managedMods.RemoveAll(mod => GetModState(mod.FileName) == ModState.Missing);
            if (removedCount > 0) SaveManifest();

            var previousOrder = previousMods.Select((mod, index) => (mod.FileName, index)).ToDictionary(pair => pair.FileName, pair => pair.index, StringComparer.OrdinalIgnoreCase);
            _managedMods = _managedMods.OrderBy(mod => previousOrder.GetValueOrDefault(mod.FileName, int.MaxValue)).ThenBy(mod => mod.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            RefreshModList(preserveDesiredStates: true);
            SetStatus($"Refreshed {_managedMods.Count} mod(s); found {discoveredCount} new mod(s); removed {removedCount} mod(s).");
        }
        catch (Exception ex)
        {
            _managedMods = previousMods;
            RefreshModList(preserveDesiredStates: true);
            ShowError("Could not refresh the mod list.", ex);
        }
    }

    private void RefreshModList(bool preserveDesiredStates = false, IReadOnlyDictionary<string, bool>? stateOverrides = null)
    {
        if (preserveDesiredStates)
            foreach (ListViewItem item in _modList.Items) _modDesiredStates[(string)item.Tag!] = item.Checked;
        if (stateOverrides is not null)
            foreach (var (fileName, isEnabled) in stateOverrides) _modDesiredStates[fileName] = isEnabled;

        _modFileSets.Clear();
        foreach (var mod in _managedMods) _modFileSets[mod.FileName] = LoadModFileSet(mod);

        var searchText = _modSearchBox.Text.Trim();
        _modListUpdating = true;
        _modList.BeginUpdate();
        try
        {
            _modList.Items.Clear();
            for (var index = 0; index < _managedMods.Count; index++)
            {
                var mod = _managedMods[index];
                if (!mod.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase)) continue;
                _modList.Items.Add(new ListViewItem(new[] { mod.DisplayName, (index + 1).ToString(), ModStateLabels[(int)GetModState(mod.FileName)], "" })
                {
                    Tag = mod.FileName,
                    Checked = _modDesiredStates.TryGetValue(mod.FileName, out var isEnabled) ? isEnabled : File.Exists(PakPath(mod.FileName))
                });
            }
        }
        finally
        {
            _modList.EndUpdate();
            _modListUpdating = false;
        }
        _fillModListColumn();
        UpdateSelectionButtons();
        AutoLinkInstalledMods();
        _modBrowser.RefreshInstallStates();
    }

    private void ToggleAllMods()
    {
        var items = _modList.Items.Cast<ListViewItem>().ToList();
        var shouldCheck = items.Any(item => !item.Checked);
        _modListUpdating = true;
        foreach (var item in items)
        {
            item.Checked = shouldCheck;
            _modDesiredStates[(string)item.Tag!] = shouldCheck;
        }
        _modListUpdating = false;
        UpdateSelectionButtons();
    }

    private bool HasPendingChanges() => _modList.Items.Cast<ListViewItem>().Any(item =>
        GetModState((string)item.Tag!) is var state && state is not (ModState.Missing or ModState.Conflict) && item.Checked != (state == ModState.Enabled));

    private void UpdateSelectionButtons()
    {
        if (_modListUpdating) return;
        var hasPending = HasPendingChanges() || HasPendingOrder();
        _changesConfirmButton.Enabled = _changesRevertButton.Enabled = hasPending;
        Theme.StyleButton(_changesConfirmButton, hasPending ? ButtonKind.Attention : ButtonKind.Normal);
        _modUninstallButton.Enabled = _modList.SelectedItems.Count > 0;
        UpdateConflictColumn();
    }

    private void ConfirmChanges()
    {
        if (!EnsureGameSelected()) return;
        var toggledCount = ApplyModChanges();
        if (toggledCount < 0) return;
        var renamedCount = ApplyLoadOrder();
        if (renamedCount < 0) return;
        SetStatus(toggledCount == 0 && renamedCount == 0 ? "No changes to confirm." : $"{toggledCount} mod(s) enabled/disabled, changed loading priority for {renamedCount} mod(s).");
    }

    private void RevertChanges()
    {
        _modDesiredStates.Clear();
        SortModsByFileName();
        RefreshModList();
        SetStatus("Returned to last save state.");
    }

    /// <summary>Files maps each file path to the hash of its contents ("" when unknown).</summary>
    private sealed record ModFileSet(Dictionary<string, string>? Files, string Source, string? Error);

    // ---- Conflict detection

    private ModFileSet LoadModFileSet(ModEntry mod)
    {
        var pakPath = ModPaths(mod.FileName).FirstOrDefault(File.Exists);
        if (pakPath is null) return new(null, "", "The mod file is missing.");
        try
        {
            var listPath = PakPath(mod.FileName + ".list");
            var pakInfo = new FileInfo(pakPath);
            var listInfo = new FileInfo(listPath);
            var stamp = $"{pakInfo.Length}|{pakInfo.LastWriteTimeUtc.Ticks}|{(listInfo.Exists ? listInfo.LastWriteTimeUtc.Ticks : 0)}";
            if (_modFileSetCache.TryGetValue(pakPath, out var cached) && cached.Stamp == stamp) return cached.FileSet;

            ModFileSet fileSet;
            try { fileSet = new(PakIndexReader.ReadPakFileList(pakPath), "file index", null); }
            catch (Exception ex) when (ex is PakFormatException or IOException or UnauthorizedAccessException)
            {
                fileSet = listInfo.Exists
                    ? new(PakIndexReader.ReadListFile(listPath), "list file", null)
                    : new(null, "", $"{ex.Message} Add a '{mod.FileName}.list' file beside the mod file to enable conflict detection.");
            }
            _modFileSetCache[pakPath] = (stamp, fileSet);
            return fileSet;
        }
        catch (Exception ex) { return new(null, "", ex.Message); }
    }

    private static string JoinNames(IEnumerable<string> names)
    {
        var all = names.ToList();
        return string.Join(", ", all.Take(3)) + (all.Count > 3 ? $" +{all.Count - 3} more" : "");
    }

    /// <summary>Compares the checked mods in list order: when several contain the same file, the last one wins.</summary>
    private void UpdateConflictColumn()
    {
        var enabledMods = new List<(ListViewItem Item, ModFileSet FileSet)>();
        foreach (ListViewItem item in _modList.Items)
            if (item.Checked && _modFileSets.TryGetValue((string)item.Tag!, out var fileSet)) enabledMods.Add((item, fileSet));

        var fileOwners = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < enabledMods.Count; index++)
        {
            if (enabledMods[index].FileSet.Files is not { } files) continue;
            foreach (var file in files.Keys)
            {
                if (!fileOwners.TryGetValue(file, out var owners)) fileOwners[file] = owners = [];
                owners.Add(index);
            }
        }
        // Mods that ship a byte-identical copy of a file (same stored hash) do not conflict: whichever loads last gives the same result.
        bool IsIdentical(string file, List<int> owners)
        {
            var hashes = owners.Select(owner => enabledMods[owner].FileSet.Files![file]).Distinct().ToList();
            return hashes.Count == 1 && hashes[0] != "";
        }
        var conflicts = fileOwners.Where(pair => pair.Value.Count > 1 && !IsIdentical(pair.Key, pair.Value))
            .Select(pair => (File: pair.Key, Winner: pair.Value[^1], Losers: pair.Value.SkipLast(1).ToList())).ToList();
        string NameOf(int index) => enabledMods[index].Item.Text;

        foreach (ListViewItem item in _modList.Items)
        {
            string text = "", tip = "";
            var color = Theme.TextColor;
            var index = item.Checked ? enabledMods.FindIndex(entry => entry.Item == item) : -1;
            if (index >= 0)
            {
                var fileSet = enabledMods[index].FileSet;
                var involved = conflicts.Where(conflict => conflict.Winner == index || conflict.Losers.Contains(index)).ToList();
                var wonCount = involved.Count(conflict => conflict.Winner == index);
                if (fileSet.Files is null)
                {
                    (text, tip, color) = ("Unknown (no readable file list)", fileSet.Error ?? "", Theme.MutedColor);
                }
                else if (involved.Count == 0)
                {
                    (text, tip) = ("None", $"File list read from the {fileSet.Source}.");
                }
                else
                {
                    var beaten = involved.Where(conflict => conflict.Winner == index).SelectMany(conflict => conflict.Losers).Select(NameOf).Distinct(StringComparer.OrdinalIgnoreCase);
                    var beatenBy = involved.Where(conflict => conflict.Winner != index).Select(conflict => NameOf(conflict.Winner)).Distinct(StringComparer.OrdinalIgnoreCase);
                    text = string.Join("; ", new[]
                    {
                        wonCount > 0 ? $"overrides {wonCount} file(s) of {JoinNames(beaten)}" : "",
                        involved.Count > wonCount ? $"overridden on {involved.Count - wonCount} file(s) by {JoinNames(beatenBy)}" : ""
                    }.Where(part => part != ""));
                    tip = string.Join("\n", involved.Take(10).Select(conflict => conflict.File)) + (involved.Count > 10 ? $"\n...and {involved.Count - 10} more" : "");
                    color = Theme.HighlightColor;
                }
            }
            if (item.SubItems[3].Text != text) item.SubItems[3].Text = text;
            item.ToolTipText = tip;
            if (item.ForeColor != color) item.ForeColor = color;
        }

        _conflictRows = conflicts.Select(conflict => new ConflictRow(conflict.File, NameOf(conflict.Winner), string.Join(", ", conflict.Losers.Select(NameOf))))
            .OrderBy(row => row.Asset, StringComparer.Ordinal).ToList();
        _conflictUnreadableMods = enabledMods.Where(entry => entry.FileSet.Files is null).Select(entry => entry.Item.Text).ToList();
        _conflictViewButton.Text = _conflictRows.Count > 0 ? $"View conflicts ({_conflictRows.Count})" : "View conflicts";
    }

    private void ShowConflicts()
    {
        ShowPopup(new ConflictsDialog(_conflictRows, _conflictUnreadableMods));
    }

    // ---- Load order

    private bool HasPendingOrder() => !_managedMods.Select(mod => mod.FileName)
        .SequenceEqual(_managedMods.Select(mod => mod.FileName).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static string StripOrderPrefix(string fileName)
    {
        var stripped = Regex.Replace(fileName, @"^\d{3,4}_", "");
        return stripped != fileName && IsSafePakFileName(stripped) && Path.GetFileNameWithoutExtension(stripped).Length > 0 ? stripped : fileName;
    }

    /// <summary>Renames every mod to "NNN_name.pak" following the list order. Returns the number of renamed mods, or -1 on failure.</summary>
    private int ApplyLoadOrder()
    {
        if (!HasPendingOrder()) return 0;
        var width = Math.Max(3, _managedMods.Count.ToString().Length);
        var plan = _managedMods
            .Select((mod, index) => (Mod: mod, NewName: $"{index + 1}".PadLeft(width, '0') + "_" + StripOrderPrefix(mod.FileName)))
            .Where(entry => entry.NewName != entry.Mod.FileName).ToList();
        if (plan.Count == 0) return 0;

        var checkedStates = _modList.Items.Cast<ListViewItem>().ToDictionary(item => (string)item.Tag!, item => item.Checked, StringComparer.OrdinalIgnoreCase);
        var renamedStates = plan.Where(entry => checkedStates.ContainsKey(entry.Mod.FileName))
            .ToDictionary(entry => entry.NewName, entry => checkedStates[entry.Mod.FileName], StringComparer.OrdinalIgnoreCase);
        try
        {
            RenameMods(plan);
            RefreshModList(preserveDesiredStates: true, stateOverrides: renamedStates);
            return plan.Count;
        }
        catch (Exception ex)
        {
            RefreshModList(preserveDesiredStates: true);
            ShowError("Could not apply the new loading priority.", ex);
            return -1;
        }
    }

    private void RenameMods(List<(ModEntry Mod, string NewName)> plan)
    {
        var moves = new List<(string Source, string Destination)>();
        foreach (var (mod, newName) in plan)
        {
            if (!IsSafePakFileName(newName)) throw new InvalidDataException($"Invalid mod file name: {newName}");
            var oldSignature = SigName(mod.FileName);
            var newSignature = SigName(newName);
            foreach (var location in ModLocations)
            {
                moves.Add((location(mod.FileName), location(newName)));
                moves.Add((location(oldSignature), location(newSignature)));
            }
            moves.Add((PakPath(mod.FileName + ".list"), PakPath(newName + ".list")));
        }

        var previousNames = plan.Select(entry => entry.Mod.FileName).ToList();
        try
        {
            MoveFiles(moves.Where(move => File.Exists(move.Source)).ToList(), () =>
            {
                foreach (var (mod, newName) in plan) mod.FileName = newName;
                SaveManifest();
            });
        }
        catch
        {
            for (var index = 0; index < plan.Count; index++) plan[index].Mod.FileName = previousNames[index];
            throw;
        }
    }

    // ---- Drag-and-drop reordering inside the list

    private void BeginModDrag(ItemDragEventArgs e)
    {
        if (e.Item is not ListViewItem dragged) return;
        if (!dragged.Selected)
        {
            _modList.SelectedItems.Clear();
            dragged.Selected = true;
        }
        var data = new DataObject();
        data.SetData(ModOrderDragFormat, _modList.SelectedItems.Cast<ListViewItem>().Select(item => (string)item.Tag!).ToArray());
        _modList.DoDragDrop(data, DragDropEffects.Move);
        _modList.DropLineIndex = -1;
    }

    private int GetInsertionIndex(DragEventArgs e)
    {
        var point = _modList.PointToClient(new Point(e.X, e.Y));
        if (_modList.GetItemAt(8, point.Y) is not { } item)
            return _modList.Items.Count == 0 || point.Y < _modList.Items[0].Bounds.Top ? 0 : _modList.Items.Count;
        return point.Y > item.Bounds.Top + item.Bounds.Height / 2 ? item.Index + 1 : item.Index;
    }

    private void HandleModDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(ModOrderDragFormat) != true) return;
        e.Effect = DragDropEffects.Move;
        var pointerY = _modList.PointToClient(new Point(e.X, e.Y)).Y;
        if (_modList.Items.Count > 0)
        {
            // Auto-scroll when the pointer is near the top or bottom edge.
            if (pointerY < 40 && _modList.TopItem is { Index: > 0 } topItem) _modList.EnsureVisible(topItem.Index - 1);
            else if (pointerY > _modList.ClientSize.Height - 16)
            {
                var lastVisible = _modList.GetItemAt(8, _modList.ClientSize.Height - 4);
                _modList.EnsureVisible(Math.Min(_modList.Items.Count - 1, (lastVisible?.Index ?? _modList.Items.Count - 1) + 1));
            }
        }
        _modList.DropLineIndex = GetInsertionIndex(e);
    }

    private void HandleModDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(ModOrderDragFormat) is not string[] fileNames) return;
        var insertionIndex = GetInsertionIndex(e);
        _modList.DropLineIndex = -1;
        ReorderMods(fileNames, insertionIndex);
    }

    private void ReorderMods(string[] fileNames, int insertionIndex)
    {
        var movedNames = fileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var movedMods = _managedMods.Where(mod => movedNames.Contains(mod.FileName)).ToList();
        if (movedMods.Count == 0) return;

        var originalOrder = _managedMods.Select(mod => mod.FileName).ToList();
        var movedBeforeInsertion = _managedMods.Take(insertionIndex).Count(mod => movedNames.Contains(mod.FileName));
        _managedMods.RemoveAll(mod => movedNames.Contains(mod.FileName));
        _managedMods.InsertRange(Math.Clamp(insertionIndex - movedBeforeInsertion, 0, _managedMods.Count), movedMods);
        if (originalOrder.SequenceEqual(_managedMods.Select(mod => mod.FileName))) return;

        RefreshModList(preserveDesiredStates: true);
        foreach (ListViewItem item in _modList.Items) if (movedNames.Contains((string)item.Tag!)) item.Selected = true;
        SetStatus("Mod position changed");
    }

    /// <summary>Accepts the game folder, its Content folder or the Paks folder itself and returns the Paks folder (or null).</summary>
    private static string? FindPaksDirectory(string gameDirectory)
    {
        if (!Directory.Exists(gameDirectory)) return null;
        var folder = new DirectoryInfo(Path.GetFullPath(gameDirectory));
        if (folder.Name.Equals("Paks", StringComparison.OrdinalIgnoreCase) && folder.Parent?.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) == true) return folder.FullName;
        if (folder.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(folder.FullName, "Paks"))) return Path.Combine(folder.FullName, "Paks");
        return new[] { Path.Combine(folder.FullName, "redout2", "Content", "Paks"), Path.Combine(folder.FullName, "Content", "Paks") }.FirstOrDefault(Directory.Exists);
    }

    private static string FindSteamGameDirectory()
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddLibrary(string? path)
        {
            if (Directory.Exists(path)) libraries.Add(Path.GetFullPath(path));
        }

        AddLibrary(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string);
        AddLibrary(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        AddLibrary(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
        foreach (var steamRoot in libraries.ToList())
        {
            var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile)) continue;
            foreach (Match match in Regex.Matches(File.ReadAllText(libraryFile), @"""(?:path|\d+)""\s+""(?<path>(?:\\.|[^""])*)""", RegexOptions.IgnoreCase))
                AddLibrary(match.Groups["path"].Value.Replace(@"\\", @"\"));
        }

        foreach (var library in libraries)
        {
            var steamApps = Path.Combine(library, "steamapps");
            var appManifest = Path.Combine(steamApps, $"appmanifest_{SteamAppId}.acf");
            var installDirectory = File.Exists(appManifest)
                ? Regex.Match(File.ReadAllText(appManifest), @"""installdir""\s+""(?<name>[^""]+)""", RegexOptions.IgnoreCase).Groups["name"].Value
                : "";
            foreach (var name in new[] { installDirectory, "Redout2", "Redout 2" })
            {
                if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name) continue;
                var candidate = Path.Combine(steamApps, "common", name);
                if (FindPaksDirectory(candidate) is not null) return candidate;
            }
        }
        return "";
    }

    /// <summary>Adds every non-base-game PAK in the Paks folder that the manifest does not know yet.</summary>
    private int DiscoverInstalledMods()
    {
        var discoveredMods = Directory.EnumerateFiles(_gameDirectoryPaks)
            .Where(IsPakImportPath)
            .Select(GetPakFileName)
            .Where(fileName => IsSafePakFileName(fileName) && FindMod(fileName) is null
                && !(fileName.StartsWith("pakchunk", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith("WindowsNoEditor.pak", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(fileName => new ModEntry { FileName = fileName, HasSignature = SignatureExists(fileName) })
            .ToList();
        if (discoveredMods.Count == 0) return 0;
        _managedMods.AddRange(discoveredMods);
        SaveManifest();
        return discoveredMods.Count;
    }
}

internal sealed class PakFormatException(string message) : Exception(message);

/// <summary>Reads the list of files inside an Unreal .pak (or a sidecar .pak.list) without extracting anything.</summary>
internal static class PakIndexReader
{
    private const uint FooterMagic = 0x5A6F12E1;
    private const int FooterSize = 44;
    private const int FooterSearchBytes = 1024;
    private const int IndexBytesMax = 512 * 1024 * 1024;
    private const int IndexEntriesMax = 10_000_000;
    private const int IndexStringCharsMax = 8192;
    private const string GamePrefix = "redout2/";
    private const string ContentPrefix = "content/";

    /// <summary>Returns every file path in the pak with the hash of its contents ("" when it cannot be read).</summary>
    public static Dictionary<string, string> ReadPakFileList(string pakPath)
    {
        using var pak = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (pak.Length < FooterSize) throw new PakFormatException("The file is too small to be a mod file.");

        var footer = new byte[(int)Math.Min(pak.Length, FooterSearchBytes)];
        pak.Seek(-footer.Length, SeekOrigin.End);
        pak.ReadExactly(footer);
        var magicOffset = -1;
        for (var offset = footer.Length - FooterSize; offset >= 0 && magicOffset < 0; offset--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(offset)) == FooterMagic) magicOffset = offset;
        if (magicOffset < 0) throw new PakFormatException("No file footer was found.");

        var version = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(magicOffset + 4));
        var indexOffset = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(magicOffset + 8));
        var indexSize = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(magicOffset + 16));
        if (version is < 3 or > 11) throw new PakFormatException($"Mod file version {version} is not supported.");
        // From version 4 the footer carries an "index is encrypted" flag right before the magic.
        if (version >= 4 && magicOffset >= 1 && footer[magicOffset - 1] != 0) throw new PakFormatException("The mod file index is encrypted.");

        var index = new IndexReader(ReadBlock(pak, indexOffset, indexSize));
        var mountPoint = index.ReadString();
        var entryCount = index.ReadCount("index entry count");
        var files = new Dictionary<string, string>(Math.Min(entryCount, 100_000), StringComparer.Ordinal);

        if (version < 10)
        {
            for (var entry = 0; entry < entryCount; entry++)
            {
                var name = index.ReadString();
                index.Skip(24); // offset, size, uncompressed size
                var compression = index.ReadInt32();
                var hash = index.ReadHash();
                if (compression != 0)
                {
                    var blockCount = index.ReadInt32();
                    if (blockCount < 0) throw new PakFormatException("The mod file index contains an invalid entry.");
                    index.Skip((long)blockCount * 16);
                }
                index.Skip(5); // encrypted flag, compression block size
                files[NormalizeKey(mountPoint + "/" + name)] = hash;
            }
            return files;
        }

        index.Skip(8); // path hash seed
        if (index.ReadInt32() != 0) index.Skip(36); // path hash index offset, size, hash
        if (index.ReadInt32() == 0) throw new PakFormatException("The mod file has no full directory index.");
        var directoryOffset = index.ReadInt64();
        var directorySize = index.ReadInt64();
        byte[]? encodedEntries = null;
        try
        {
            index.Skip(20); // directory index hash
            encodedEntries = index.ReadBytes(index.ReadInt32());
        }
        catch (PakFormatException) { } // only needed to compare file contents; without it every shared file counts as a conflict
        var directoryIndex = new IndexReader(ReadBlock(pak, directoryOffset, directorySize));
        var directoryCount = directoryIndex.ReadCount("directory index");
        for (var directoryNumber = 0; directoryNumber < directoryCount; directoryNumber++)
        {
            var directory = directoryIndex.ReadString();
            var fileCount = directoryIndex.ReadCount("directory index");
            for (var fileNumber = 0; fileNumber < fileCount; fileNumber++)
            {
                var fileName = directoryIndex.ReadString();
                var entryOffset = directoryIndex.ReadInt32(); // position in the encoded entries
                files[NormalizeKey(mountPoint + "/" + directory + "/" + fileName)] = encodedEntries is null ? "" : ContentHash(pak, encodedEntries, entryOffset);
            }
        }
        return files;
    }

    public static Dictionary<string, string> ReadListFile(string listPath)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadLines(listPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';' || line.StartsWith("//", StringComparison.Ordinal)) continue;

            var quoteStart = line.IndexOf('"');
            var quoteEnd = line.IndexOf('"', quoteStart + 1);
            if (quoteEnd > quoteStart) line = line[(quoteStart + 1)..quoteEnd];

            var key = NormalizeKey(line);
            var lastSlash = key.LastIndexOf('/');
            if (lastSlash < 0 || key.IndexOf('.', lastSlash) < 0) continue; // not a file path (directory, log noise, "Mount point ..." lines)

            files[key.StartsWith(GamePrefix, StringComparison.Ordinal) ? key
                : key.StartsWith(ContentPrefix, StringComparison.Ordinal) ? GamePrefix + key
                : GamePrefix + ContentPrefix + key] = "";
        }
        return files;
    }

    private static string HashId(ReadOnlySpan<byte> hash) => hash.IndexOfAnyExcept((byte)0) < 0 ? "" : Convert.ToHexString(hash);

    /// <summary>
    /// The SHA1 stored with a file's data, or "" when it cannot be determined (which counts as "different"). Decodes the bit-packed index entry
    /// to find the file's record in the pak, and only trusts the hash if the sizes in that record match the index.
    /// </summary>
    private static string ContentHash(FileStream pak, byte[] encoded, int entryOffset)
    {
        try
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(entryOffset));
            var position = entryOffset + 4 + ((bits & 0x3F) == 0x3F ? 4 : 0); // 0x3F: the compression block size follows explicitly

            long Next(uint flag32)
            {
                var is32 = (bits & flag32) != 0;
                var value = is32 ? BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(position)) : (long)BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(position));
                position += is32 ? 4 : 8;
                return value;
            }

            var offset = Next(1u << 31);
            var uncompressedSize = Next(1u << 30);
            var size = ((bits >> 23) & 0x3F) != 0 ? Next(1u << 29) : uncompressedSize;

            var record = new byte[48]; // offset, size, uncompressed size, compression, SHA1
            if (RandomAccess.Read(pak.SafeFileHandle, record, offset) != record.Length
                || BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(8)) != size
                || BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(16)) != uncompressedSize) return "";
            return HashId(record.AsSpan(28, 20));
        }
        catch { return ""; }
    }

    private static string NormalizeKey(string path) =>
        string.Join("/", path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .SkipWhile(part => part is "." or "..").Where(part => part != ".")).ToLowerInvariant();

    private static byte[] ReadBlock(FileStream pak, long offset, long size)
    {
        if (offset < 0 || size <= 0 || size > IndexBytesMax || offset > pak.Length - size)
            throw new PakFormatException("The mod file index location is invalid.");
        var block = new byte[size];
        pak.Seek(offset, SeekOrigin.Begin);
        pak.ReadExactly(block);
        return block;
    }

    private sealed class IndexReader(byte[] data)
    {
        private int _offset;

        public int ReadInt32()
        {
            Require(4);
            var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(_offset));
            _offset += 4;
            return value;
        }

        public string ReadHash()
        {
            Require(20);
            var hash = HashId(data.AsSpan(_offset, 20));
            _offset += 20;
            return hash;
        }

        public byte[] ReadBytes(int count)
        {
            Require(count);
            var bytes = data.AsSpan(_offset, count).ToArray();
            _offset += count;
            return bytes;
        }

        public long ReadInt64()
        {
            Require(8);
            var value = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(_offset));
            _offset += 8;
            return value;
        }

        public int ReadCount(string description)
        {
            var count = ReadInt32();
            return count is < 0 or > IndexEntriesMax ? throw new PakFormatException($"The mod file {description} is invalid.") : count;
        }

        public void Skip(long count)
        {
            Require(count);
            _offset += (int)count;
        }

        /// <summary>Unreal FString: positive length = UTF-8, negative length = UTF-16, both null-terminated.</summary>
        public string ReadString()
        {
            var length = ReadInt32();
            if (length == 0) return "";
            var charCount = Math.Abs((long)length);
            if (charCount > IndexStringCharsMax) throw new PakFormatException("The mod file index contains an invalid string.");
            var isUtf8 = length > 0;
            var byteCount = (int)(isUtf8 ? charCount : charCount * 2);
            Require(byteCount);
            var text = (isUtf8 ? Encoding.UTF8 : Encoding.Unicode).GetString(data, _offset, byteCount - (isUtf8 ? 1 : 2));
            _offset += byteCount;
            return text;
        }

        private void Require(long count)
        {
            if (count < 0 || count > data.Length - _offset) throw new PakFormatException("The mod file index is truncated or corrupt.");
        }
    }
}

/// <summary>
/// Reads a mod archive. ZIP uses the framework; RAR and 7z use SharpCompress.
/// Entry paths are always returned with forward slashes and only list files, never folders.
/// </summary>
internal abstract class ModArchive : IDisposable
{
    public static readonly string[] Extensions = [".zip", ".rar", ".7z"];

    public static bool IsSupported(string path) => Extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every file in the archive.</summary>
    public abstract IReadOnlyList<string> EntryPaths { get; }

    /// <summary>
    /// A copy action for the entry (it writes the file to a path that must not exist yet),
    /// or null when the archive has no such entry. The match ignores case.
    /// </summary>
    public abstract Action<string>? CopyEntry(string entryPath);

    public abstract void Dispose();

    public static ModArchive Open(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        try
        {
            return path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? new ZipModArchive(path) : new SharpModArchive(path);
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
        {
            // A damaged, password-protected or unsupported archive: SharpCompress and ZipFile throw a variety of exception types.
            throw new InvalidDataException($"Could not read this {extension} archive. It may be damaged, password protected, or in an unsupported variant ({ex.Message})", ex);
        }
    }

    protected static string Normalize(string path) => path.Replace('\\', '/');

    protected static void CopyTo(Stream input, string destination)
    {
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try { input.CopyTo(output); }
        catch
        {
            output.Dispose();
            File.Delete(destination); // never leave a half-written file behind
            throw;
        }
    }

    private sealed class ZipModArchive : ModArchive
    {
        private readonly ZipArchive _archive;
        private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public ZipModArchive(string path)
        {
            _archive = ZipFile.OpenRead(path);
            foreach (var entry in _archive.Entries)
                if (entry.Name != "") _entries.TryAdd(Normalize(entry.FullName), entry);
        }

        public override IReadOnlyList<string> EntryPaths => [.. _entries.Keys];

        public override Action<string>? CopyEntry(string entryPath) =>
            _entries.TryGetValue(Normalize(entryPath), out var entry)
                ? destination =>
                {
                    using var input = entry.Open();
                    CopyTo(input, destination);
                }
                : null;

        public override void Dispose() => _archive.Dispose();
    }

    private sealed class SharpModArchive : ModArchive
    {
        private readonly IArchive _archive;
        private readonly Dictionary<string, IArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _extracted = new(StringComparer.OrdinalIgnoreCase);
        private string? _extractDirectory;

        public SharpModArchive(string path)
        {
            _archive = ArchiveFactory.Open(path);
            try
            {
                foreach (var entry in _archive.Entries)
                    if (!entry.IsDirectory && entry.Key is { Length: > 0 } key) _entries.TryAdd(Normalize(key), entry);
                // Solid archives can only be read in order: reading single entries at random fails with a size mismatch.
                // So they are unpacked once, front to back, into a private temporary folder.
                if (_archive.IsSolid) ExtractInOrder();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void ExtractInOrder()
        {
            _extractDirectory = Path.Combine(Path.GetTempPath(), "R2MM-archive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_extractDirectory);
            using var reader = _archive.ExtractAllEntries();
            var index = 0;
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory || reader.Entry.Key is not { Length: > 0 } key) continue;
                // Files are stored under numbers, never under their entry names, so a crafted path cannot escape the folder.
                var target = Path.Combine(_extractDirectory, $"{index++}.tmp");
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) reader.WriteEntryTo(output);
                _extracted.TryAdd(Normalize(key), target);
            }
        }

        public override IReadOnlyList<string> EntryPaths => [.. _entries.Keys];

        public override Action<string>? CopyEntry(string entryPath)
        {
            var key = Normalize(entryPath);
            if (_extracted.TryGetValue(key, out var extractedFile))
                return destination =>
                {
                    using var input = new FileStream(extractedFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                    CopyTo(input, destination);
                };
            return _entries.TryGetValue(key, out var entry)
                ? destination =>
                {
                    try
                    {
                        using var input = entry.OpenEntryStream();
                        CopyTo(input, destination);
                    }
                    catch (Exception ex) when (ex is SharpCompress.Common.CryptographicException or System.Security.Cryptography.CryptographicException)
                    {
                        throw new InvalidDataException("This archive is password protected, so R2MM cannot read it. Extract it yourself and drop the .pak files instead.", ex);
                    }
                }
                : null;
        }

        public override void Dispose()
        {
            _archive.Dispose();
            if (_extractDirectory is null) return;
            try { Directory.Delete(_extractDirectory, recursive: true); }
            catch { } // temporary files; Windows cleans the temp folder eventually
        }
    }
}
