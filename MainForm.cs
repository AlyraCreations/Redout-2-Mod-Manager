using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace R2MM;

internal sealed partial class MainForm : Form
{
    private static readonly Color BackgroundColor = Theme.Background;
    private static readonly Color PanelColor = Theme.Panel;
    private static readonly Color ButtonColor = Theme.Button;
    private static readonly Color TextColor = Theme.Text;
    private static readonly Color MutedColor = Theme.Muted;
    private static readonly Color AccentColor = Theme.Accent;
    private static readonly Color BorderColor = Theme.Border;
    private static readonly Color ButtonHoverColor = Theme.ButtonHover;
    private static readonly Color AccentHoverColor = Theme.AccentHover;
    private const string SteamLaunchUri = "steam://rungameid/1799930";
    private const string NexusPage = "https://www.nexusmods.com/redout2";
    private const string GameBananaPage = "https://gamebanana.com/games/24088";

    private readonly string _settingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "R2MM");
    private readonly TextBox _gamePathBox = new();
    private readonly TextBox _searchBox = new();
    private readonly Dictionary<string, bool> _desiredStates = new(StringComparer.OrdinalIgnoreCase);
    private string _gameExecutable = "";
    private readonly ListView _modList = new BufferedListView();
    private readonly Label _statusLabel = new();
    private readonly TextBox _consoleHistory = new();
    private Panel? _consolePanel;
    private Button? _consoleCloseButton;
    private bool _consoleExpanded;
    private readonly Button _confirmButton;
    private readonly Button _revertButton;
    private readonly Button _removeButton;
    private readonly Button _conflictsButton;
    private readonly System.Windows.Forms.Timer _columnTimer = new() { Interval = 40 };
    private readonly Dictionary<string, ModFileSet> _fileSets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Stamp, ModFileSet Set)> _fileSetCache = new(StringComparer.OrdinalIgnoreCase);
    private List<ConflictRow> _conflictRows = [];
    private List<string> _unreadableMods = [];
    private readonly List<ModEntry> _mods = [];
    private string _gameDirectory = "";
    private string _paksDirectory = "";
    private string _manifestPath = "";
    private bool _initializing;
    private Bitmap? _backgroundArt;
    private bool _wallpaperEnabled = true;
    private string _customWallpaperPath = "";
    private readonly List<Button> _sidebarButtons = [];
    private Panel? _sidebarPanel;
    private bool _resizingColumns;
    private bool _liveResizing;
    private int _sourceCleanupFailures;

    #region Import and drag/drop operations
    private void EnableFileDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += HandleFileDragEnter;
        control.DragDrop += HandleFileDrop;
        foreach (Control child in control.Controls)
        {
            EnableFileDrop(child);
        }
    }

    private void HandleFileDragEnter(object? sender, DragEventArgs e)
    {
        var data = e.Data;
        if (data is not null && data.GetDataPresent(ModDragFormat))
        {
            e.Effect = sender == _modList ? DragDropEffects.Move : DragDropEffects.None;
            return;
        }
        if (data is not null &&
            data.GetData(DataFormats.FileDrop) is string[] paths &&
            paths.Any(IsSupportedImportPath))
        {
            e.Effect = DragDropEffects.Copy;
            return;
        }
        e.Effect = DragDropEffects.None;
    }

    private void HandleFileDrop(object? sender, DragEventArgs e)
    {
        var data = e.Data;
        if (data is not null && data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            if (EnsureGameSelected())
            {
                ImportModFiles(paths.OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToList());
            }
        }
    }

    private static bool IsSupportedImportPath(string path)
    {
        return IsPakImportPath(path) ||
            Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPakImportPath(string path) =>
        Path.GetExtension(path).Equals(".pak", StringComparison.OrdinalIgnoreCase) ||
        IsDisabledPakPath(path);

    private static bool IsDisabledPakPath(string path) =>
        Path.GetFileName(path).EndsWith(".pak.disabled", StringComparison.OrdinalIgnoreCase);

    private static string GetPakFileName(string path)
    {
        var fileName = Path.GetFileName(path);
        return IsDisabledPakPath(path) ? fileName[..^".disabled".Length] : fileName;
    }

    private static string GetSourceSignaturePath(string sourcePakPath, bool isDisabledPak)
    {
        if (!isDisabledPak)
        {
            return Path.ChangeExtension(sourcePakPath, ".sig");
        }

        var directory = Path.GetDirectoryName(sourcePakPath)!;
        var pakBaseName = Path.GetFileName(sourcePakPath)[..^".pak.disabled".Length];
        return Path.Combine(directory, pakBaseName + ".sig.disabled");
    }

    private void InitializeManager()
    {
        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            var settingsPath = Path.Combine(_settingsDirectory, "settings.json");
            var savedDirectory = "";
            var savedExecutable = "";
            var savedWallpaper = "";
            if (File.Exists(settingsPath))
            {
                var settings = JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(settingsPath));
                savedDirectory = settings?.GameDirectory ?? "";
                savedExecutable = settings?.GameExecutable ?? "";
                savedWallpaper = settings?.WallpaperPath ?? "";
            }
            if (!string.IsNullOrWhiteSpace(savedExecutable)) _gameExecutable = savedExecutable;
            if (!string.IsNullOrWhiteSpace(savedWallpaper) && File.Exists(savedWallpaper))
            {
                _customWallpaperPath = savedWallpaper;
                _backgroundArt?.Dispose();
                _backgroundArt = LoadBackgroundArt(savedWallpaper);
            }

            var initialDirectory = FindPaksDirectory(savedDirectory) is not null
                ? savedDirectory
                : FindDefaultGameDirectory();
            if (!string.IsNullOrWhiteSpace(initialDirectory))
            {
                SetGameDirectory(initialDirectory, saveSettings: false);
            }
            else
            {
                _gamePathBox.Text = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Steam", "steamapps", "common", "Redout2");
                SetStatus("Redout 2 was not found in your Steam libraries. Use Browse... to select it.");
            }
            if (!string.IsNullOrWhiteSpace(savedExecutable)) _gameExecutable = savedExecutable;
        }
        catch (Exception ex)
        {
            ShowError("Could not initialize the manager.", ex);
        }
    }

    private string FindDefaultGameDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Steam registry detection requires Windows.");
        }

        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registeredSteamPath = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
            "SteamPath",
            null) as string;
        if (!string.IsNullOrWhiteSpace(registeredSteamPath))
        {
            AddDirectory(steamRoots, registeredSteamPath);
        }
        AddDirectory(
            steamRoots,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        AddDirectory(
            steamRoots,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));

        var libraries = new HashSet<string>(steamRoots, StringComparer.OrdinalIgnoreCase);
        foreach (var steamRoot in steamRoots)
        {
            var libraryFoldersFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFoldersFile))
            {
                continue;
            }

            var contents = File.ReadAllText(libraryFoldersFile);
            AddVdfPaths(libraries, contents, "\"path\"\\s+\"(?<path>(?:\\\\.|[^\"])*)\"");
            AddVdfPaths(libraries, contents, "\"\\d+\"\\s+\"(?<path>(?:\\\\.|[^\"])*)\"");
        }

        foreach (var library in libraries)
        {
            var steamApps = Path.Combine(library, "steamapps");
            var manifestPath = Path.Combine(steamApps, "appmanifest_1799930.acf");
            if (File.Exists(manifestPath))
            {
                var manifest = File.ReadAllText(manifestPath);
                var installDirectory = Regex.Match(
                    manifest,
                    "\"installdir\"\\s+\"(?<name>[^\"]+)\"",
                    RegexOptions.IgnoreCase).Groups["name"].Value;
                if (!string.IsNullOrWhiteSpace(installDirectory) &&
                    Path.GetFileName(installDirectory).Equals(installDirectory, StringComparison.Ordinal))
                {
                    var installedGame = Path.Combine(steamApps, "common", installDirectory);
                    if (FindPaksDirectory(installedGame) is not null)
                    {
                        return installedGame;
                    }
                }
            }

            foreach (var name in new[] { "Redout2", "Redout 2" })
            {
                var candidate = Path.Combine(steamApps, "common", name);
                if (FindPaksDirectory(candidate) is not null)
                {
                    return candidate;
                }
            }
        }
        return "";
    }

    private static void AddDirectory(HashSet<string> directories, string path)
    {
        if (Directory.Exists(path))
        {
            directories.Add(Path.GetFullPath(path));
        }
    }

    private static void AddVdfPaths(HashSet<string> directories, string contents, string pattern)
    {
        foreach (Match match in Regex.Matches(contents, pattern, RegexOptions.IgnoreCase))
        {
            var path = match.Groups["path"].Value.Replace(@"\\", @"\");
            AddDirectory(directories, path);
        }
    }

    private void BrowseForGameFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the Redout 2 installation folder",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_gameDirectory) ? _gameDirectory : ""
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            SetGameDirectory(dialog.SelectedPath, saveSettings: true);
        }
        catch (Exception ex)
        {
            ShowError("Could not use that game folder.", ex);
        }
    }

    private void SetGameDirectory(string gameDirectory, bool saveSettings)
    {
        var paksDirectory = FindPaksDirectory(gameDirectory)
            ?? throw new DirectoryNotFoundException(
                "Could not find Redout 2's Content\\Paks folder. Select the game folder, its redout2 subfolder, the Content folder, or the Paks folder itself.");

        _gameDirectory = Path.GetFullPath(gameDirectory);
        _desiredStates.Clear();
        _gameExecutable = FindGameExecutable(_gameDirectory);
        _paksDirectory = paksDirectory;
        _manifestPath = Path.Combine(_settingsDirectory, $"{GetInstallKey(_paksDirectory)}-mods.json");
        _gamePathBox.Text = _gameDirectory;
        LoadManifest();
        DiscoverInstalledMods();
        RefreshModList();
        if (saveSettings)
        {
            SaveSettings();
        }
    }

    #endregion

    #region Manifest, settings and mod installation
    private void LoadManifest()
    {
        _mods.Clear();
        if (!File.Exists(_manifestPath))
        {
            return;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<ModEntry>>(File.ReadAllText(_manifestPath))
                ?? throw new InvalidDataException("The saved mod list is empty or invalid.");
            foreach (var entry in entries)
            {
                if (!IsSafePakFileName(entry.FileName))
                {
                    throw new InvalidDataException($"The saved mod list contains an invalid PAK name: {entry.FileName}");
                }
                if (_mods.Any(mod => mod.FileName.Equals(entry.FileName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException($"The saved mod list contains a duplicate PAK: {entry.FileName}");
                }
                _mods.Add(entry);
            }

            SortModsByFileName();
        }
        catch (Exception ex)
        {
            _mods.Clear();
            throw new InvalidDataException($"Could not load the saved mod list at '{_manifestPath}'.", ex);
        }
    }

    private void SaveManifest()
    {
        var temporaryPath = _manifestPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_mods, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _manifestPath, overwrite: true);
    }

    private void SaveSettings()
    {
        var settings = new ManagerSettings { GameDirectory = _gameDirectory, GameExecutable = _gameExecutable, WallpaperPath = _customWallpaperPath };
        var path = Path.Combine(_settingsDirectory, "settings.json");
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private void ImportModFiles(IEnumerable<string> paths)
    {
        _sourceCleanupFailures = 0;
        var orderWasPending = HasPendingOrder();
        var imported = 0;
        var alreadyManaged = 0;
        var restoredMods = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var extension = Path.GetExtension(path);
                if (IsPakImportPath(path))
                {
                    var fileName = GetPakFileName(path);
                    var wasManaged = _mods.Any(mod =>
                        mod.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
                    if (ImportPak(path))
                    {
                        imported++;
                        if (wasManaged)
                        {
                            restoredMods[fileName] = !IsDisabledPakPath(path);
                        }
                    }
                    else
                    {
                        alreadyManaged++;
                    }
                }
                else if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var result = InstallArchive(path);
                    imported += result.Imported;
                    alreadyManaged += result.AlreadyManaged;
                    foreach (var (fileName, enabled) in result.RestoredStates)
                    {
                        restoredMods[fileName] = enabled;
                    }
                }
                else
                {
                    throw new InvalidDataException("Choose a .pak, .pak.disabled, or a ZIP containing .pak or .pak.disabled mods.");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        if (!orderWasPending)
        {
            SortModsByFileName();
            try
            {
                SaveManifest();
            }
            catch (Exception)
            {
            }
        }
        RefreshModList(preserveDesiredStates: true, stateOverrides: restoredMods);
        if (failures.Count == 0)
        {
            var cleanupNote = _sourceCleanupFailures > 0
                ? $" Could not remove {_sourceCleanupFailures} original file(s) after copying them."
                : "";
            SetStatus((imported == 0
                ? $"No new mods imported; already managed: {alreadyManaged}."
                : $"Imported or restored {imported} mod(s); already managed: {alreadyManaged}.") + cleanupNote);
            return;
        }

        SetStatus($"Imported or restored {imported} mod(s); {failures.Count} failed.");
        MessageBox.Show(
            this,
            $"Imported or restored {imported} mod(s).\r\n\r\nCould not import:\r\n{string.Join("\r\n", failures)}",
            "Mod import results",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private (int Imported, int AlreadyManaged, Dictionary<string, bool> RestoredStates) InstallArchive(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var pakEntries = archive.Entries
            .Where(entry => IsPakImportPath(entry.FullName.Replace('\\', '/')))
            .ToList();
        if (pakEntries.Count == 0)
        {
            throw new InvalidDataException("The ZIP does not contain a .pak or .pak.disabled mod file.");
        }

        var filesToCopy = new List<(ZipArchiveEntry Entry, string Target)>();
        var newMods = new List<ModEntry>();
        var restoredMods = new List<(ModEntry Mod, bool PreviousHasSignature, bool HasSignature, bool Enabled)>();
        var alreadyManaged = 0;
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pakEntry in pakEntries)
        {
            var entryPath = pakEntry.FullName.Replace('\\', '/');
            var isDisabledPak = IsDisabledPakPath(entryPath);
            var fileName = GetPakFileName(entryPath);
            if (!IsSafePakFileName(fileName))
            {
                throw new InvalidDataException("The archive contains an invalid PAK file name.");
            }
            if (!fileNames.Add(fileName))
            {
                throw new InvalidDataException($"The ZIP contains multiple PAKs with the same filename: {fileName}");
            }
            var managedMod = _mods.FirstOrDefault(mod =>
                mod.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            if (managedMod is not null)
            {
                if (IsManagedPakInstalled(managedMod))
                {
                    alreadyManaged++;
                    continue;
                }
                fileName = managedMod.FileName;
            }
            else
            {
                EnsureNotRegisteredOrInstalled(fileName);
            }

            var signatureEntry = FindSignatureEntry(archive, pakEntry, fileName);
            var signatureName = Path.ChangeExtension(fileName, ".sig");
            var signatureTarget = isDisabledPak ? GetDisabledPath(signatureName) : Path.Combine(_paksDirectory, signatureName);
            if (managedMod is not null)
            {
                var hasSignature = ValidateRestoredSignature(managedMod, isDisabledPak, signatureEntry is not null);
                restoredMods.Add((managedMod, managedMod.HasSignature, hasSignature, !isDisabledPak));
            }
            else
            {
                if (signatureEntry is not null &&
                    (File.Exists(Path.Combine(_paksDirectory, signatureName)) || File.Exists(GetDisabledPath(signatureName))))
                {
                    throw new IOException($"A signature file already exists: {Path.GetFileName(signatureTarget)}");
                }
                newMods.Add(new ModEntry
                {
                    FileName = fileName,
                    DisplayName = Path.GetFileNameWithoutExtension(fileName),
                    HasSignature = signatureEntry is not null
                });
            }
            filesToCopy.Add((pakEntry, isDisabledPak ? GetDisabledPath(fileName) : Path.Combine(_paksDirectory, fileName)));
            if (signatureEntry is not null)
            {
                filesToCopy.Add((signatureEntry, signatureTarget));
            }
            var listEntry = FindSidecarEntry(archive, pakEntry, fileName);
            var listTarget = Path.Combine(_paksDirectory, fileName + ".list");
            if (listEntry is not null && !File.Exists(listTarget))
            {
                filesToCopy.Add((listEntry, listTarget));
            }
        }

        if (filesToCopy.Count == 0)
        {
            return (0, alreadyManaged, new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));
        }

        var createdFiles = new List<string>();
        try
        {
            foreach (var (entry, target) in filesToCopy)
            {
                CopyArchiveEntry(entry, target);
                createdFiles.Add(target);
            }

            _mods.AddRange(newMods);
            foreach (var restoredMod in restoredMods)
            {
                restoredMod.Mod.HasSignature = restoredMod.HasSignature;
            }
            SaveManifest();
        }
        catch
        {
            foreach (var mod in newMods)
            {
                _mods.Remove(mod);
            }
            foreach (var restoredMod in restoredMods)
            {
                restoredMod.Mod.HasSignature = restoredMod.PreviousHasSignature;
            }
            foreach (var path in createdFiles)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            throw;
        }
        return (newMods.Count + restoredMods.Count, alreadyManaged,
            restoredMods.ToDictionary(mod => mod.Mod.FileName, mod => mod.Enabled, StringComparer.OrdinalIgnoreCase));
    }

    private bool ImportPak(string sourcePath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var isDisabledPak = IsDisabledPakPath(fullSourcePath);
        var fileName = GetPakFileName(fullSourcePath);
        if (!IsSafePakFileName(fileName))
        {
            throw new InvalidDataException($"Invalid PAK filename: {fileName}");
        }

        var managedMod = _mods.FirstOrDefault(mod =>
            mod.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (managedMod is not null)
        {
            return RestoreManagedPak(managedMod, fullSourcePath);
        }

        if (Path.GetDirectoryName(fullSourcePath)!.Equals(_paksDirectory, StringComparison.OrdinalIgnoreCase))
        {
            if (isDisabledPak)
            {
                RegisterExistingDisabledPak(fullSourcePath, fileName);
            }
            else
            {
                RegisterExistingPak(fullSourcePath);
            }
            return true;
        }

        EnsureNotRegisteredOrInstalled(fileName);
        var sourceSignature = GetSourceSignaturePath(fullSourcePath, isDisabledPak);
        var hasSignature = File.Exists(sourceSignature);
        var targetPak = isDisabledPak ? GetDisabledPath(fileName) : Path.Combine(_paksDirectory, fileName);
        var signatureName = Path.ChangeExtension(fileName, ".sig");
        var targetSignature = isDisabledPak ? GetDisabledPath(signatureName) : Path.Combine(_paksDirectory, signatureName);
        if (hasSignature &&
            (File.Exists(Path.Combine(_paksDirectory, signatureName)) || File.Exists(GetDisabledPath(signatureName))))
        {
            throw new IOException($"A signature file already exists: {Path.GetFileName(targetSignature)}");
        }

        var copiedFiles = new List<string>();
        try
        {
            File.Copy(fullSourcePath, targetPak, overwrite: false);
            copiedFiles.Add(targetPak);
            if (hasSignature)
            {
                File.Copy(sourceSignature, targetSignature, overwrite: false);
                copiedFiles.Add(targetSignature);
            }

            var sourceList = Path.Combine(Path.GetDirectoryName(fullSourcePath)!, fileName + ".list");
            var targetList = Path.Combine(_paksDirectory, fileName + ".list");
            if (File.Exists(sourceList) && !File.Exists(targetList))
            {
                File.Copy(sourceList, targetList, overwrite: false);
                copiedFiles.Add(targetList);
            }

            AddManagedMod(fileName, hasSignature);
            RemoveMovedSources(
                fullSourcePath,
                hasSignature ? sourceSignature : null,
                copiedFiles.Contains(targetList) ? sourceList : null);
            return true;
        }
        catch
        {
            foreach (var path in copiedFiles)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            throw;
        }
    }

    private void RemoveMovedSources(params string?[] paths)
    {
        foreach (var path in paths)
        {
            if (path is null)
            {
                continue;
            }
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                _sourceCleanupFailures++;
            }
        }
    }

    private bool IsManagedPakInstalled(ModEntry mod)
    {
        var activePak = Path.Combine(_paksDirectory, mod.FileName);
        var renamedDisabledPak = GetDisabledPath(mod.FileName);
        var legacyDisabledPak = GetLegacyDisabledPath(mod.FileName);
        var isActive = File.Exists(activePak);
        var isRenamedDisabled = File.Exists(renamedDisabledPak);
        var isLegacyDisabled = File.Exists(legacyDisabledPak);
        if ((isActive && (isRenamedDisabled || isLegacyDisabled)) ||
            (isRenamedDisabled && isLegacyDisabled))
        {
            throw new IOException($"'{mod.FileName}' has conflicting enabled and disabled copies. Resolve the conflict before importing it.");
        }
        return isActive || isRenamedDisabled || isLegacyDisabled;
    }

    private bool ValidateRestoredSignature(ModEntry mod, bool isDisabledPak, bool hasSourceSignature)
    {
        var signatureName = Path.ChangeExtension(mod.FileName, ".sig");
        var targetSignature = isDisabledPak ? GetDisabledPath(signatureName) : Path.Combine(_paksDirectory, signatureName);
        var otherSignature = isDisabledPak ? Path.Combine(_paksDirectory, signatureName) : GetDisabledPath(signatureName);
        if (File.Exists(otherSignature))
        {
            throw new IOException($"A signature file in the opposite enabled state already exists: {Path.GetFileName(otherSignature)}");
        }
        var hasTargetSignature = File.Exists(targetSignature);
        if (hasSourceSignature && hasTargetSignature)
        {
            throw new IOException($"A signature file already exists: {Path.GetFileName(targetSignature)}");
        }
        if (mod.HasSignature && !hasSourceSignature && !hasTargetSignature)
        {
            throw new FileNotFoundException(
                $"'{mod.FileName}' requires its matching .sig file. Import the PAK and signature together.",
                targetSignature);
        }
        if (!mod.HasSignature && hasTargetSignature)
        {
            throw new IOException($"A signature file already exists: {Path.GetFileName(targetSignature)}");
        }
        return hasSourceSignature || hasTargetSignature;
    }

    private bool RestoreManagedPak(ModEntry mod, string sourcePath)
    {
        if (IsManagedPakInstalled(mod))
        {
            return false;
        }

        var isDisabledPak = IsDisabledPakPath(sourcePath);
        var sourceSignature = GetSourceSignaturePath(sourcePath, isDisabledPak);
        var hasSourceSignature = File.Exists(sourceSignature);
        var targetPak = isDisabledPak ? GetDisabledPath(mod.FileName) : Path.Combine(_paksDirectory, mod.FileName);
        var signatureName = Path.ChangeExtension(mod.FileName, ".sig");
        var targetSignature = isDisabledPak ? GetDisabledPath(signatureName) : Path.Combine(_paksDirectory, signatureName);
        var hasSignature = ValidateRestoredSignature(mod, isDisabledPak, hasSourceSignature);

        var copiedFiles = new List<string>();
        var previousHasSignature = mod.HasSignature;
        try
        {
            File.Copy(sourcePath, targetPak, overwrite: false);
            copiedFiles.Add(targetPak);
            if (hasSourceSignature)
            {
                File.Copy(sourceSignature, targetSignature, overwrite: false);
                copiedFiles.Add(targetSignature);
            }

            mod.HasSignature = hasSignature;
            SaveManifest();
            if (!Path.GetDirectoryName(Path.GetFullPath(sourcePath))!.Equals(_paksDirectory, StringComparison.OrdinalIgnoreCase))
            {
                RemoveMovedSources(sourcePath, hasSourceSignature ? sourceSignature : null);
            }
            return true;
        }
        catch
        {
            mod.HasSignature = previousHasSignature;
            foreach (var path in copiedFiles)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            throw;
        }
    }

    private static ZipArchiveEntry? FindSignatureEntry(ZipArchive archive, ZipArchiveEntry pakEntry, string pakFileName)
    {
        var normalizedPath = pakEntry.FullName.Replace('\\', '/');
        var directory = Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/') ?? "";
        var signatureName = Path.ChangeExtension(pakFileName, ".sig");
        if (IsDisabledPakPath(normalizedPath))
        {
            signatureName += ".disabled";
        }
        var fullSignaturePath = string.IsNullOrEmpty(directory) ? signatureName : $"{directory}/{signatureName}";
        return archive.Entries.FirstOrDefault(entry =>
            entry.FullName.Replace('\\', '/').Equals(fullSignaturePath, StringComparison.OrdinalIgnoreCase));
    }

    private static ZipArchiveEntry? FindSidecarEntry(ZipArchive archive, ZipArchiveEntry pakEntry, string pakFileName)
    {
        var normalizedPath = pakEntry.FullName.Replace('\\', '/');
        var directory = Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/') ?? "";
        var listName = pakFileName + ".list";
        var fullListPath = string.IsNullOrEmpty(directory) ? listName : $"{directory}/{listName}";
        return archive.Entries.FirstOrDefault(entry =>
            entry.FullName.Replace('\\', '/').Equals(fullListPath, StringComparison.OrdinalIgnoreCase));
    }

    private static void CopyArchiveEntry(ZipArchiveEntry entry, string destination)
    {
        using var input = entry.Open();
        var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            using (output)
            {
                input.CopyTo(output);
            }
        }
        catch
        {
            File.Delete(destination);
            throw;
        }
    }

    private void RegisterExistingPak(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        if (!IsSafePakFileName(fileName))
        {
            throw new InvalidDataException($"Invalid PAK filename: {fileName}");
        }
        if (!Path.GetDirectoryName(fullPath)!.Equals(_paksDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only PAK files directly inside the game's Content\\Paks folder can be registered.");
        }
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected PAK file no longer exists.", fullPath);
        }
        if (_mods.Any(mod => mod.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"'{fileName}' is already in this manager's list.");
        }
        if (File.Exists(GetDisabledPath(fileName)) || File.Exists(GetLegacyDisabledPath(fileName)))
        {
            throw new IOException($"A disabled mod with this name is already stored: {fileName}");
        }

        var signaturePath = Path.Combine(_paksDirectory, Path.ChangeExtension(fileName, ".sig"));
        AddManagedMod(fileName, File.Exists(signaturePath));
    }

    private void RegisterExistingDisabledPak(string fullPath, string fileName)
    {
        var disabledPak = GetDisabledPath(fileName);
        if (!Path.GetFullPath(fullPath).Equals(disabledPak, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only disabled PAKs directly inside the game's Content\\Paks folder can be registered.");
        }
        if (!File.Exists(disabledPak))
        {
            throw new FileNotFoundException("The selected disabled PAK file no longer exists.", disabledPak);
        }
        if (File.Exists(Path.Combine(_paksDirectory, fileName)) ||
            File.Exists(GetLegacyDisabledPath(fileName)))
        {
            throw new IOException($"'{fileName}' already has another enabled or disabled copy.");
        }

        var signatureName = Path.ChangeExtension(fileName, ".sig");
        AddManagedMod(fileName, File.Exists(GetDisabledPath(signatureName)));
    }

    private void AddManagedMod(string fileName, bool hasSignature)
    {
        var mod = new ModEntry
        {
            FileName = fileName,
            DisplayName = Path.GetFileNameWithoutExtension(fileName),
            HasSignature = hasSignature
        };
        _mods.Add(mod);
        try
        {
            SaveManifest();
        }
        catch
        {
            _mods.Remove(mod);
            throw;
        }
    }

    private void EnsureNotRegisteredOrInstalled(string fileName)
    {
        if (_mods.Any(mod => mod.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"'{fileName}' is already in this manager's list.");
        }
        if (File.Exists(Path.Combine(_paksDirectory, fileName)))
        {
            throw new IOException(
                $"'{fileName}' already exists in the game Paks folder. Import that file to register it without replacing it.");
        }
        if (File.Exists(GetDisabledPath(fileName)) || File.Exists(GetLegacyDisabledPath(fileName)))
        {
            throw new IOException($"A disabled mod with this name is already stored: {fileName}");
        }
    }

    private int ApplyModChanges()
    {

        var desiredStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (ListViewItem item in _modList.Items)
        {
            if (item.Tag is string fileName)
            {
                desiredStates[fileName] = item.Checked;
            }
        }

        var changed = 0;
        try
        {
            foreach (var mod in _mods)
            {
                var activePak = Path.Combine(_paksDirectory, mod.FileName);
                var disabledPak = GetDisabledPath(mod.FileName);
                var legacyDisabledPak = GetLegacyDisabledPath(mod.FileName);
                var isActive = File.Exists(activePak);
                var isDisabled = File.Exists(disabledPak);
                var isLegacyDisabled = File.Exists(legacyDisabledPak);
                if (!isActive && !isDisabled && !isLegacyDisabled)
                {
                    continue;
                }
                if ((isActive && (isDisabled || isLegacyDisabled)) ||
                    (isDisabled && isLegacyDisabled))
                {
                    throw new IOException(
                        $"Cannot determine the state of '{mod.FileName}'. Its enabled and disabled copies are conflicting or missing.");
                }
                if (!desiredStates.TryGetValue(mod.FileName, out var shouldBeEnabled) ||
                    shouldBeEnabled == isActive)
                {
                    continue;
                }

                TransferModFiles(mod, enable: shouldBeEnabled, fromLegacyDisabled: isLegacyDisabled);
                changed++;
            }

            RefreshModList();
            return changed;
        }
        catch (Exception ex)
        {
            RefreshModList(preserveDesiredStates: true);
            ShowError("Could not apply all mod changes.", ex);
            return -1;
        }
    }

    private void TransferModFiles(ModEntry mod, bool enable, bool fromLegacyDisabled)
    {
        var sourcePak = enable
            ? fromLegacyDisabled ? GetLegacyDisabledPath(mod.FileName) : GetDisabledPath(mod.FileName)
            : Path.Combine(_paksDirectory, mod.FileName);
        var destinationPak = enable ? Path.Combine(_paksDirectory, mod.FileName) : GetDisabledPath(mod.FileName);
        var sourceFiles = new List<(string Source, string Destination)> { (sourcePak, destinationPak) };
        if (mod.HasSignature)
        {
            var signatureName = Path.ChangeExtension(mod.FileName, ".sig");
            var sourceSignature = enable
                ? fromLegacyDisabled ? GetLegacyDisabledPath(signatureName) : GetDisabledPath(signatureName)
                : Path.Combine(_paksDirectory, signatureName);
            var destinationSignature = enable
                ? Path.Combine(_paksDirectory, signatureName)
                : GetDisabledPath(signatureName);
            sourceFiles.Add((sourceSignature, destinationSignature));
        }

        foreach (var pair in sourceFiles)
        {
            if (!File.Exists(pair.Source))
            {
                throw new FileNotFoundException("A managed mod file is missing.", pair.Source);
            }
            if (File.Exists(pair.Destination))
            {
                throw new IOException($"Refusing to overwrite an existing file: {pair.Destination}");
            }
        }

        var movedFiles = new List<(string Source, string Destination)>();
        try
        {
            foreach (var pair in sourceFiles)
            {
                MoveManagedFile(pair.Source, pair.Destination);
                movedFiles.Add(pair);
            }
        }
        catch (Exception operationError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var pair in movedFiles.AsEnumerable().Reverse())
            {
                try
                {
                    MoveManagedFile(pair.Destination, pair.Source);
                }
                catch (Exception ex)
                {
                    rollbackErrors.Add(ex);
                }
            }
            if (rollbackErrors.Count > 0)
            {
                rollbackErrors.Insert(0, operationError);
                throw new AggregateException("The rename failed and rollback was incomplete.", rollbackErrors);
            }
            throw;
        }
    }

    private static void MoveManagedFile(string source, string destination)
    {
        if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            File.Move(source, destination);
            return;
        }

        File.Copy(source, destination, overwrite: false);
        try
        {
            File.Delete(source);
        }
        catch (Exception moveError)
        {
            try
            {
                DeleteIfExists(destination);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Could not complete the cross-volume move or clean up its copy.", moveError, cleanupError);
            }
            throw;
        }
    }

    #endregion

    #region Applying, enabling, disabling and uninstalling mods
    private void UninstallSelectedMods()
    {
        if (_modList.SelectedItems.Count == 0)
        {
            return;
        }

        var selectedNames = _modList.SelectedItems.Cast<ListViewItem>()
            .Select(item => item.Tag as string)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedMods = _mods.Where(mod => selectedNames.Contains(mod.FileName)).ToList();
        var prompt = $"Uninstall {selectedMods.Count} selected mod(s)? Their managed PAK and optional signature files will be deleted.";
        if (MessageBox.Show(this, prompt, "Confirm uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            foreach (var mod in selectedMods)
            {
                DeleteIfExists(Path.Combine(_paksDirectory, mod.FileName));
                DeleteIfExists(GetDisabledPath(mod.FileName));
                DeleteIfExists(GetLegacyDisabledPath(mod.FileName));
                DeleteIfExists(Path.Combine(_paksDirectory, mod.FileName + ".list"));
                if (mod.HasSignature)
                {
                    var signatureName = Path.ChangeExtension(mod.FileName, ".sig");
                    DeleteIfExists(Path.Combine(_paksDirectory, signatureName));
                    DeleteIfExists(GetDisabledPath(signatureName));
                    DeleteIfExists(GetLegacyDisabledPath(signatureName));
                }
                _mods.Remove(mod);
                SaveManifest();
            }

            RefreshModList();
            SetStatus($"Uninstalled {selectedMods.Count} mod(s).");
        }
        catch (Exception ex)
        {
            RefreshModList();
            ShowError("Could not uninstall all selected mods.", ex);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string FindGameExecutable(string folder)
    {
        var candidates = new[]
        {
            Path.Combine(folder, "Redout2.exe"),
            Path.Combine(folder, "redout2", "Binaries", "Win64", "Redout2-Win64-Shipping.exe"),
            Path.Combine(folder, "Binaries", "Win64", "Redout2-Win64-Shipping.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private void StartGame()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = SteamLaunchUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError("Could not launch Redout 2 through Steam.", ex);
        }
    }

    private void OpenNexusPage() => OpenUrl(NexusPage);

    private bool EnsureGameSelected()
    {
        if (!string.IsNullOrEmpty(_paksDirectory) && Directory.Exists(_paksDirectory))
        {
            return true;
        }
        MessageBox.Show(
            this,
            "Select your Redout 2 installation folder first.",
            "Game folder required",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return false;
    }

    #endregion

    #region Refreshing, sorting and mod-list state
    private void RefreshMods()
    {
        if (!EnsureGameSelected())
        {
            return;
        }

        var previousMods = _mods.ToList();
        try
        {
            LoadManifest();
            var discoveredMods = DiscoverInstalledMods();
            var missingMods = _mods.Where(mod =>
                !File.Exists(Path.Combine(_paksDirectory, mod.FileName)) &&
                !File.Exists(GetDisabledPath(mod.FileName)) &&
                !File.Exists(GetLegacyDisabledPath(mod.FileName))).ToList();
            foreach (var missingMod in missingMods)
            {
                _mods.Remove(missingMod);
            }
            if (missingMods.Count > 0)
            {
                SaveManifest();
            }

            var previousOrder = previousMods.Select((mod, index) => (mod.FileName, index))
                .ToDictionary(pair => pair.FileName, pair => pair.index, StringComparer.OrdinalIgnoreCase);
            _mods.Sort((a, b) =>
            {
                var left = previousOrder.TryGetValue(a.FileName, out var x) ? x : int.MaxValue;
                var right = previousOrder.TryGetValue(b.FileName, out var y) ? y : int.MaxValue;
                return left != right
                    ? left.CompareTo(right)
                    : string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            });

            RefreshModList(preserveDesiredStates: true);
            SetStatus($"Refreshed {_mods.Count} mod(s); found {discoveredMods} new mod(s); removed {missingMods.Count} mod(s).");
        }
        catch (Exception ex)
        {
            _mods.Clear();
            _mods.AddRange(previousMods);
            RefreshModList(preserveDesiredStates: true);
            ShowError("Could not refresh the mod list.", ex);
        }
    }

    private void RefreshModList(bool preserveDesiredStates = false, IReadOnlyDictionary<string, bool>? stateOverrides = null)
    {
        var desiredStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (preserveDesiredStates)
        {
            foreach (ListViewItem item in _modList.Items)
                if (item.Tag is string fileName) _desiredStates[fileName] = item.Checked;
        }
        foreach (var pair in _desiredStates) desiredStates[pair.Key] = pair.Value;

        if (stateOverrides is not null)
        {
            foreach (var (fileName, enabled) in stateOverrides)
            {
                desiredStates[fileName] = enabled;
            }
        }

        LoadFileSets();
        _initializing = true;
        _modList.BeginUpdate();
        try
        {
            _modList.Items.Clear();
            var position = 0;
            foreach (var mod in _mods)
            {
                position++;
                if (!string.IsNullOrWhiteSpace(_searchBox.Text) &&
                    !mod.DisplayName.Contains(_searchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                var item = new ListViewItem(mod.DisplayName) { Tag = mod.FileName };
                item.Checked = desiredStates.TryGetValue(mod.FileName, out var shouldBeEnabled)
                    ? shouldBeEnabled
                    : File.Exists(Path.Combine(_paksDirectory, mod.FileName));
                item.SubItems.Add(position.ToString());
                item.SubItems.Add(GetModStatus(mod));
                item.SubItems.Add("");
                _modList.Items.Add(item);
            }
        }
        finally
        {
            _modList.EndUpdate();
            _initializing = false;
        }
        ResizeLastColumn();
        UpdateSelectionButtons();
    }

    private string GetModStatus(ModEntry mod)
    {
        var isActive = File.Exists(Path.Combine(_paksDirectory, mod.FileName));
        var isRenamedDisabled = File.Exists(GetDisabledPath(mod.FileName));
        var isLegacyDisabled = File.Exists(GetLegacyDisabledPath(mod.FileName));
        if (isActive && !isRenamedDisabled && !isLegacyDisabled)
        {
            return "Enabled";
        }
        if (!isActive && isRenamedDisabled && !isLegacyDisabled)
        {
            return "Disabled";
        }
        if (!isActive && !isRenamedDisabled && isLegacyDisabled)
        {
            return "Disabled (legacy)";
        }
        return "Missing / conflict";
    }

    private void ToggleAllMods()
    {
        var shouldCheck = _modList.Items.Cast<ListViewItem>().Any(item => !item.Checked);
        _initializing = true;
        _modList.BeginUpdate();
        try
        {
            foreach (ListViewItem item in _modList.Items)
            {
                item.Checked = shouldCheck;
                if (item.Tag is string name) _desiredStates[name] = shouldCheck;
            }
        }
        finally
        {
            _modList.EndUpdate();
            _initializing = false;
        }
        UpdateSelectionButtons();
    }

    private bool HasPendingChanges()
    {
        foreach (ListViewItem item in _modList.Items)
        {
            if (item.Tag is not string fileName)
            {
                continue;
            }

            var isActive = File.Exists(Path.Combine(_paksDirectory, fileName));
            var isRenamedDisabled = File.Exists(GetDisabledPath(fileName));
            var isLegacyDisabled = File.Exists(GetLegacyDisabledPath(fileName));
            if ((isActive && (isRenamedDisabled || isLegacyDisabled)) ||
                (!isActive && !isRenamedDisabled && !isLegacyDisabled) ||
                (isRenamedDisabled && isLegacyDisabled))
            {
                continue;
            }

            if (item.Checked != isActive)
            {
                return true;
            }
        }
        return false;
    }

    #endregion

    #region Paths, status and shared models
    private string GetDisabledPath(string fileName)
    {
        return Path.Combine(_paksDirectory, fileName + ".disabled");
    }

    private string GetLegacyDisabledPath(string fileName) =>
        Path.Combine(_settingsDirectory, "DisabledMods", GetInstallKey(_paksDirectory), fileName);

    private static string GetInstallKey(string paksDirectory)
    {
        var normalized = Path.GetFullPath(paksDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    private static bool IsSafePakFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal) &&
        Path.GetExtension(fileName).Equals(".pak", StringComparison.OrdinalIgnoreCase) &&
        fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    #endregion

    #region Data models
    private sealed class ManagerSettings
    {
        public string GameDirectory { get; set; } = "";
        public string GameExecutable { get; set; } = "";
        public string WallpaperPath { get; set; } = "";
    }

    private sealed class ModEntry
    {
        public string FileName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool HasSignature { get; set; }
    }
    #endregion
}
