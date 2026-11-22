using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using SharpImage = SixLabors.ImageSharp.Image;
using SixLabors.ImageSharp.Processing;

namespace R2MM;

// Mod browser integration: which installed PAKs belong to which Nexus mod, and the download / update / uninstall flows.
internal sealed partial class MainForm
{
    private const string NexusKeyUrl = "https://www.nexusmods.com/settings/api-keys";

    private readonly NexusClient _nexus;
    private readonly ModBrowserPanel _modBrowser;
    private List<NexusLink> _nexusLinks = [];
    private string _nexusLinksFile = "";
    private string _nexusApiKey = "";
    private bool _nexusKeyPromptShown;
    private bool? _nexusIsPremium;
    private readonly ModBrowserWindow _browserWindow;
    private readonly Button _browserButton;

    private static ManagerSettings? ReadSettings()
    {
        try { return File.Exists(SettingsFile) ? JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(SettingsFile)) : null; }
        catch { return null; }
    }

    // ---- Browser window

    /// <summary>Opens the mod browser; pressing the button again while it is open closes it.</summary>
    private void OpenModBrowser()
    {
        if (_browserWindow.Visible && _browserWindow.WindowState != FormWindowState.Minimized)
        {
            _browserWindow.Hide();
            return;
        }
        if (!_browserWindow.Visible)
        {
            if (!_browserWindow.IsHandleCreated)
            {
                _browserWindow.FitToColumns(_modBrowser.Columns);
                // First time: put it beside the main window, or on screen if there is no room.
                var area = Screen.FromControl(this).WorkingArea;
                var height = Math.Min(_browserWindow.Height, area.Height);
                var x = Right + 8 + _browserWindow.Width <= area.Right ? Right + 8 : Math.Max(area.Left, area.Right - _browserWindow.Width);
                _browserWindow.Bounds = new Rectangle(x, Math.Max(area.Top, Math.Min(Top, area.Bottom - height)), _browserWindow.Width, height);
            }
            _browserWindow.Show(this);
        }
        if (_browserWindow.WindowState == FormWindowState.Minimized) _browserWindow.WindowState = FormWindowState.Normal;
        _browserWindow.Activate();
    }

    /// <summary>The sidebar button carries the update count the drawer's badge used to show.</summary>
    private void UpdateBrowserButton()
    {
        var updates = _modBrowser.UpdateCount;
        _browserButton.Text = updates > 0 ? $"Mod browser ({updates})" : "Mod browser";
        Theme.StyleButton(_browserButton, updates > 0 ? ButtonKind.Attention : ButtonKind.Normal);
    }

    // ---- API key

    private void ConfigureNexusApiKey()
    {
        using var dialog = new NexusKeyDialog(_nexusApiKey,
            "Browsing mods needs no key. Downloading and updating from inside R2MM needs a personal API key and a Nexus Mods Premium account; " +
            "Nexus only gives direct download links to Premium members. Without a key (or on a free account) R2MM opens the mod's page so you can " +
            "download the archive yourself and drop it onto R2MM. Leave the box empty to remove the key.",
            "Cancel", () => OpenShell(NexusKeyUrl, "Could not open the web page."));
        if (dialog.ShowDialog(DialogParent) == DialogResult.OK) SetNexusApiKey(dialog.ApiKey);
    }

    private void SetNexusApiKey(string key, bool validate = true)
    {
        _nexusApiKey = key;
        _nexus.ApiKey = key;
        _nexusIsPremium = null;
        try { SaveSettings(); } catch (Exception ex) { ShowError("Could not save the API key.", ex); }
        if (key == "") SetStatus("Nexus API key removed.");
        else if (validate) _ = ValidateNexusKeyAsync(interactive: true);
    }

    private async Task ValidateNexusKeyAsync(bool interactive)
    {
        try
        {
            var user = await _nexus.ValidateKeyAsync();
            _nexusIsPremium = user.IsPremium;
            SetStatus(user.IsPremium ? $"Nexus Mods: signed in as {user.Name} (Premium, direct downloads enabled)."
                : $"Nexus Mods: signed in as {user.Name} (free account: downloads open the Nexus page).");
        }
        catch (NexusApiKeyException ex)
        {
            SetStatus(ex.Message);
            if (interactive) MessageBox.Show(DialogParent, ex.Message, "Nexus Mods", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) { SetStatus($"Could not check the Nexus API key: {ex.Message}"); }
    }

    // ---- Links between Nexus mods and installed PAKs

    private void LoadNexusLinks()
    {
        try
        {
            _nexusLinks = File.Exists(_nexusLinksFile)
                ? (JsonSerializer.Deserialize<List<NexusLink>>(File.ReadAllText(_nexusLinksFile)) ?? []).Where(link => link.ModId > 0).ToList()
                : [];
        }
        catch { _nexusLinks = []; } // links only drive the browser's install state; a damaged file just means "not installed"
    }

    private void SaveNexusLinks()
    {
        try { SaveJson(_nexusLinksFile, _nexusLinks); } catch { }
    }

    private NexusLink? FindNexusLink(int modId) => _nexusLinks.FirstOrDefault(link => link.ModId == modId);

    /// <summary>The managed mods behind a link. Matching ignores the NNN_ load-order prefix so reordering never breaks a link.</summary>
    private List<ModEntry> ResolveLinkedMods(NexusLink link) =>
        _managedMods.Where(mod => link.PakFiles.Contains(StripOrderPrefix(mod.FileName), StringComparer.OrdinalIgnoreCase)).ToList();

    private bool IsOnDisk(ModEntry mod) => GetModState(mod.FileName) != ModState.Missing;

    /// <summary>True while at least one PAK of the link is really present in the game folder.</summary>
    private bool IsLinkInstalled(NexusLink link) => ResolveLinkedMods(link).Any(IsOnDisk);

    private ModInstallInfo? GetNexusInstallInfo(int modId) =>
        FindNexusLink(modId) is { } link && IsLinkInstalled(link) ? new ModInstallInfo(link.Version) : null;

    // Parts of a file or mod name that say nothing about which mod it is: versions, "Redout 2", "mod", "pak", "patch".
    private static readonly Regex NoisePattern = new(@"\bv?\d+(?:\.\d+)+\b|\bv\d+\b|redout\s*2?|\bmod\b|\bpak\b|\bpatch\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PatchSuffixPattern = new(@"[_\-\s]p$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CamelCasePattern = new(@"(?<=[\p{Ll}\d])(?=\p{Lu})", RegexOptions.Compiled);
    private static readonly Regex NonWordPattern = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>The distinct lower-case words of a name, without noise, brackets, punctuation or the underscores and camel-casing of file names.</summary>
    private static HashSet<string> Words(string text) =>
        NonWordPattern.Split(NoisePattern.Replace(CamelCasePattern.Replace(text, " ").Replace('_', ' '), " ").ToLowerInvariant()).Where(word => word != "").ToHashSet();

    /// <summary>Words are the same when equal, or one letter apart (typos such as "Tokio" for "Tokyo") for words of 4+ letters.</summary>
    private static bool SameWord(string a, string b)
    {
        if (a == b) return true;
        if (Math.Min(a.Length, b.Length) < 4 || Math.Abs(a.Length - b.Length) > 1) return false;
        if (a.Length < b.Length) (a, b) = (b, a);
        var i = 0;
        while (i < b.Length && a[i] == b[i]) i++;
        return a.Length == b.Length ? a.AsSpan(i + 1).SequenceEqual(b.AsSpan(i + 1)) : a.AsSpan(i + 1).SequenceEqual(b.AsSpan(i));
    }

    /// <summary>How well two word sets describe the same mod (0 to 1): the harmonic mean of how much of each side the other one covers, so repeated or extra words only cost a little.</summary>
    private static double MatchScore(HashSet<string> pakWords, HashSet<string> modWords)
    {
        if (pakWords.Sum(word => word.Length) < 4 || modWords.Sum(word => word.Length) < 4) return 0;
        var modCovered = (double)modWords.Count(mod => pakWords.Any(pak => SameWord(pak, mod))) / modWords.Count;
        var pakCovered = (double)pakWords.Count(pak => modWords.Any(mod => SameWord(pak, mod))) / pakWords.Count;
        return modCovered + pakCovered == 0 ? 0 : 2 * modCovered * pakCovered / (modCovered + pakCovered);
    }

    private string _linkScanSignature = "";

    /// <summary>
    /// Scans the installed mods against the Nexus list, like the PAK scan does for the mods folder. It runs at launch, whenever the mod
    /// list changes and when a Nexus list arrives. It (1) drops links whose PAK files are gone and (2) recognises mods that are installed
    /// but not linked (for example after the link file was deleted). A PAK file holds nothing that names its Nexus mod, so recognition
    /// goes by name: ignoring the load-order number, a trailing "_P", versions, case, punctuation and bracketed tags, the words of the PAK
    /// name must match the words of a Nexus mod name (repeated or extra words and one-letter typos are tolerated), and no other mod may fit about as well. Recognised mods have no known version; the card offers
    /// "Reinstall" to learn it.
    /// </summary>
    private void AutoLinkInstalledMods()
    {
        try
        {
            string Signature() => string.Join('|', _managedMods.Select(mod => $"{mod.FileName}:{(int)GetModState(mod.FileName)}")) + $"#{_modBrowser.ModCount}#{_nexusLinks.Count}";
            if (_managedMods.Count == 0 || Signature() == _linkScanSignature) return;

            var dropped = _nexusLinks.RemoveAll(link => !IsLinkInstalled(link));
            if (dropped > 0) SaveNexusLinks();

            var recognised = 0;
            if (_modBrowser.ModCount > 0)
            {
                var claimed = _nexusLinks.SelectMany(link => link.PakFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var catalog = _modBrowser.Mods.Select(mod => (Mod: mod, Words: Words(mod.Name))).ToList();
                var matches = new Dictionary<int, List<string>>();
                foreach (var entry in _managedMods.Where(IsOnDisk))
                {
                    var pakName = StripOrderPrefix(entry.FileName);
                    if (claimed.Contains(pakName)) continue;
                    var pakWords = Words(PatchSuffixPattern.Replace(Path.GetFileNameWithoutExtension(pakName), ""));
                    var best = catalog.Select(item => (item.Mod, Score: MatchScore(pakWords, item.Words))).Where(item => item.Score >= 0.75)
                        .OrderByDescending(item => item.Score).Take(2).ToList();
                    if (best.Count == 0 || (best.Count == 2 && best[1].Score > best[0].Score - 0.05)) continue; // no fit, or not clear which mod it is
                    var mod = best[0].Mod;
                    if (FindNexusLink(mod.ModId) is not null) continue; // already linked through a real install
                    if (!matches.TryGetValue(mod.ModId, out var paks)) matches[mod.ModId] = paks = [];
                    paks.Add(pakName);
                }
                foreach (var (modId, paks) in matches) UpsertNexusLink(modId, "", paks);
                recognised = matches.Count;
            }

            _linkScanSignature = Signature();
            if (recognised > 0 || dropped > 0)
                SetStatus($"Nexus scan: {recognised} installed mod(s) recognised, {dropped} link(s) to removed mods dropped.");
        }
        catch (Exception ex) { SetStatus($"Could not match installed mods to Nexus mods: {ex.Message}"); }
    }

    private void UpsertNexusLink(int modId, string version, IEnumerable<string> pakFiles)
    {
        _nexusLinks.RemoveAll(link => link.ModId == modId);
        _nexusLinks.Add(new NexusLink { ModId = modId, Version = version, PakFiles = pakFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList() });
        SaveNexusLinks();
    }

    // ---- Download / update from the browser

    private async Task NexusInstallAsync(NexusMod mod, bool isUpdate, IProgress<string> progress)
    {
        if (!EnsureGameSelected()) return;
        if (_nexusApiKey == "")
        {
            // The key prompt is only shown the first time; after that Download goes straight to the mod's Nexus page.
            if (_nexusKeyPromptShown)
            {
                OpenNexusFilesPage(mod);
                return;
            }
            _nexusKeyPromptShown = true;
            try { SaveSettings(); } catch { }
            using var dialog = new NexusKeyDialog("",
                "Downloading from inside R2MM needs a personal Nexus Mods API key and a Premium account. If you choose \"Open Nexus page\", the mod's " +
                "page opens instead: download the archive there and drop it onto R2MM, which links it to the mod so updates are tracked.\r\n\r\n" +
                "You can always enter your API key later in Options > Nexus API key. This message is only shown once.",
                "Open Nexus page", () => OpenShell(NexusKeyUrl, "Could not open the web page."));
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ApiKey == "")
            {
                OpenNexusFilesPage(mod);
                return;
            }
            SetNexusApiKey(dialog.ApiKey, validate: false);
            progress.Report("Checking key...");
            await ValidateNexusKeyAsync(interactive: false);
        }
        if (_nexusIsPremium == false)
        {
            OpenNexusFilesPage(mod);
            return;
        }

        string? archivePath = null;
        try
        {
            progress.Report("Finding file...");
            var file = NexusClient.PickMainFile(await _nexus.GetFilesAsync(mod.ModId))
                ?? throw new InvalidDataException("This mod has no downloadable file on Nexus Mods.");
            var url = await _nexus.GetDownloadUrlAsync(mod.ModId, file.FileId);

            archivePath = Path.Combine(Path.GetTempPath(), "R2MM-downloads", $"{Guid.NewGuid():N}-{SafeFileName(file.FileName, file.FileId)}");
            progress.Report("Downloading...");
            await _nexus.DownloadFileAsync(url, archivePath, new Progress<double>(fraction => progress.Report($"Downloading {(int)(fraction * 100)}%")));

            progress.Report("Installing...");
            var tally = new ImportTally();
            InstallNexusArchive(mod.ModId, mod.Version, archivePath, replaceExisting: isUpdate, tally);
            RefreshModList(preserveDesiredStates: true, stateOverrides: tally.RestoredStates);
            SetStatus($"{(isUpdate ? "Updated" : "Installed")} {mod.Name} {mod.Version}.");
        }
        catch (NexusPremiumRequiredException)
        {
            _nexusIsPremium = false;
            OpenNexusFilesPage(mod);
        }
        finally
        {
            if (archivePath is not null) NexusClient.TryDelete(archivePath);
        }
    }

    private static string SafeFileName(string fileName, int fileId)
    {
        var name = Path.GetFileName(fileName);
        name = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return name == "" || Path.GetExtension(name) == "" ? $"file-{fileId}.zip" : name;
    }

    /// <summary>The fallback for accounts that cannot download through the API: open the mod's Files tab.</summary>
    private void OpenNexusFilesPage(NexusMod mod)
    {
        if (!OpenShell(NexusClient.FilesPageUrl(mod.ModId), "Could not open the web page.")) return;
        SetStatus($"Opened {mod.Name} on Nexus Mods. Download the archive there and drop it onto R2MM.");
    }

    // ---- Installing or replacing a Nexus mod from an archive

    /// <summary>A dropped archive whose name Nexus generated is linked to its mod, and replaces the installed version when it is newer.</summary>
    private void ImportNexusAwareArchive(string path, ImportTally tally)
    {
        if (!NexusArchiveName.TryParse(Path.GetFileName(path), out var modId, out var version) || (_modBrowser.ModCount > 0 && !_modBrowser.HasMod(modId)))
        {
            ImportArchive(path, tally);
            return;
        }
        var link = FindNexusLink(modId);
        var replace = link is not null && ResolveLinkedMods(link).Count > 0 && NexusVersion.IsNewer(version, link.Version);
        InstallNexusArchive(modId, version, path, replace, tally);
    }

    /// <summary>
    /// Installs the archive (ZIP, RAR or 7z) or PAK and links it to the Nexus mod. With replaceExisting the previously linked PAKs are set aside first and put
    /// back if the install fails; the new PAKs keep the old load-order slot and enabled state. Throws on failure with nothing changed.
    /// </summary>
    private void InstallNexusArchive(int modId, string version, string archivePath, bool replaceExisting, ImportTally tally)
    {
        var isArchive = ModArchive.IsSupported(archivePath);
        if (!isArchive && !IsPakImportPath(archivePath))
            throw new InvalidDataException($"R2MM can install .zip, .rar, .7z or .pak files, but this download is a {Path.GetExtension(archivePath)} file. Extract it and drop the .pak onto R2MM.");
        if (isArchive)
        {
            // Checked before anything is replaced, so a bad download never costs the installed version.
            using var archive = ModArchive.Open(archivePath);
            if (!archive.EntryPaths.Any(IsPakImportPath)) throw new InvalidDataException("The archive does not contain a .pak or .pak.disabled mod file.");
        }

        var link = FindNexusLink(modId);
        List<ModEntry> linkedMods = link is null ? [] : ResolveLinkedMods(link);
        List<ModEntry> oldMods = replaceExisting ? linkedMods : [];
        List<string> keptPakFiles = replaceExisting || link is null ? [] : link.PakFiles.Where(name => linkedMods.Any(mod => StripOrderPrefix(mod.FileName).Equals(name, StringComparison.OrdinalIgnoreCase))).ToList();
        var orderWasPending = HasPendingOrder();
        var wasEnabled = oldMods.Count == 0 || oldMods.Any(mod => GetModState(mod.FileName) == ModState.Enabled);
        var oldPrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in oldMods) oldPrefixes.TryAdd(StripOrderPrefix(mod.FileName), mod.FileName[..^StripOrderPrefix(mod.FileName).Length]);

        var snapshot = _managedMods.ToList();
        var stashDirectory = Path.Combine(Path.GetTempPath(), "R2MM-replaced-" + Guid.NewGuid().ToString("N"));
        var stashed = new List<(string Original, string Stored)>();
        var firstNewName = tally.FileNames.Count;
        try
        {
            if (oldMods.Count > 0)
            {
                stashed = StashMods(oldMods, stashDirectory);
                _managedMods.RemoveAll(oldMods.Contains);
                SaveManifest();
            }
            if (isArchive) ImportArchive(archivePath, tally);
            else ImportLoosePak(archivePath, tally);
            if (tally.FileNames.Count == firstNewName)
                throw new InvalidDataException("Nothing was installed: a mod with the same file name is already in your mod list. Uninstall it first, then try again.");
        }
        catch
        {
            UnstashFiles(stashed);
            if (oldMods.Count > 0)
            {
                _managedMods = snapshot;
                try { SaveManifest(); } catch { }
                RefreshModList(preserveDesiredStates: true);
            }
            throw;
        }

        var installed = tally.FileNames.Skip(firstNewName).Select(FindMod).OfType<ModEntry>().ToList();
        if (!orderWasPending && oldPrefixes.Count > 0)
        {
            // Keep the slot in the load order the old version had.
            var fallbackPrefix = oldPrefixes.Values.FirstOrDefault(prefix => prefix != "") ?? "";
            var plan = installed
                .Where(mod => StripOrderPrefix(mod.FileName) == mod.FileName)
                .Select(mod => (Mod: mod, Prefix: oldPrefixes.GetValueOrDefault(mod.FileName, fallbackPrefix)))
                .Where(entry => entry.Prefix != "")
                .Select(entry => (entry.Mod, NewName: entry.Prefix + entry.Mod.FileName)).ToList();
            if (plan.Count > 0)
            {
                try { RenameMods(plan); }
                catch (Exception ex) { SetStatus($"Updated, but could not keep the old load order position: {ex.Message}"); }
            }
        }
        if (!orderWasPending) SortModsByFileName();
        if (!wasEnabled)
        {
            foreach (var mod in installed.Where(mod => GetModState(mod.FileName) == ModState.Enabled))
            {
                try
                {
                    MoveFiles(ModNames(mod).Select(name => (PakPath(name), DisabledPath(name))).Where(move => File.Exists(move.Item1)).ToList());
                    tally.RestoredStates[mod.FileName] = false;
                }
                catch (Exception ex) { SetStatus($"Could not keep {mod.DisplayName} disabled: {ex.Message}"); }
            }
        }
        try { SaveManifest(); } catch { }

        UpsertNexusLink(modId, version, installed.Select(mod => StripOrderPrefix(mod.FileName)).Concat(keptPakFiles));
        try { if (Directory.Exists(stashDirectory)) Directory.Delete(stashDirectory, recursive: true); } catch { }
    }

    /// <summary>Moves every file of the mods into a temporary folder so they can be put back if the replacement fails.</summary>
    private List<(string Original, string Stored)> StashMods(IReadOnlyList<ModEntry> mods, string stashDirectory)
    {
        Directory.CreateDirectory(stashDirectory);
        var stashed = new List<(string Original, string Stored)>();
        try
        {
            foreach (var mod in mods)
            {
                foreach (var path in AllPaths(mod).Where(File.Exists))
                {
                    var stored = Path.Combine(stashDirectory, $"{stashed.Count}_{Path.GetFileName(path)}");
                    MoveFile(path, stored);
                    stashed.Add((path, stored));
                }
            }
        }
        catch
        {
            UnstashFiles(stashed);
            throw;
        }
        return stashed;
    }

    private static void UnstashFiles(IEnumerable<(string Original, string Stored)> stashed)
    {
        foreach (var (original, stored) in stashed.Reverse())
        {
            try { if (File.Exists(stored)) MoveFile(stored, original); }
            catch { } // nothing more can be done; the stash folder keeps the file
        }
    }

    // ---- Uninstall from the browser

    private void NexusUninstall(NexusMod mod)
    {
        if (FindNexusLink(mod.ModId) is not { } link) return;
        var mods = ResolveLinkedMods(link);
        if (mods.Count > 0 && !UninstallMods(mods, $"Uninstall '{mod.Name}'? Its mod files and any signature files will be deleted.")) return;
        _nexusLinks.Remove(link);
        SaveNexusLinks();
    }
}

/// <summary>What the manager knows about an installed Nexus mod.</summary>
internal sealed record ModInstallInfo(string Version);

/// <summary>
/// A drawer on the right edge of the window that lists the Redout 2 mods on Nexus Mods.
/// It only does the presenting: installing and uninstalling is delegated to the owner through the callbacks below.
/// </summary>
internal sealed class ModBrowserPanel : Panel
{
    private const int PageSize = 24, CardGap = 8, CardMargin = 8, MinCardWidth = 168, WideCardWidth = 300, MaxColumns = 7;
    private const string Dot = " \u00B7 ";
    // Wide cards put the thumbnail beside the text; narrower ones (several columns) stack everything vertically.
    private static bool IsWide(int cardWidth) => cardWidth >= WideCardWidth;
    private static int ThumbnailHeight(int cardWidth) => (cardWidth - 16) * 9 / 16;
    private static int CardHeightFor(int cardWidth) => IsWide(cardWidth) ? 158 : ThumbnailHeight(cardWidth) + 194;

    /// <summary>The narrowest client width at which this many columns still fit.</summary>
    public static int RequiredClientWidth(int columns) =>
        CardMargin * 2 + columns * MinCardWidth + (columns - 1) * CardGap + SystemInformation.VerticalScrollBarWidth;

    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromMinutes(30);
    private static readonly Font TitleFont = Theme.CreateFont(12, FontStyle.Bold);
    private static readonly Font NameFont = Theme.CreateFont(9.5f, FontStyle.Bold);
    private static readonly Font SmallFont = Theme.CreateFont(8.25f);
    private static readonly Font HeaderFont = Theme.CreateFont(9); // search box, drop-downs and the Refresh button share one size

    private readonly NexusClient _client;
    private readonly Label _title = new() { Text = "MOD BROWSER", Font = TitleFont, ForeColor = Theme.TextColor, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };
    private readonly Label _summary = new() { Font = SmallFont, ForeColor = Theme.MutedColor, AutoEllipsis = true, UseMnemonic = false };
    private readonly Label _message = new() { Font = Theme.CreateFont(9), ForeColor = Theme.MutedColor, TextAlign = ContentAlignment.TopCenter, UseMnemonic = false, Visible = false };
    private readonly TextBox _search = new() { PlaceholderText = "Search mods..." };
    private readonly Panel _searchHost;
    private readonly ComboBox _sort, _filter, _columnsCombo;
    private readonly Button _refreshButton, _moreButton;
    private readonly BufferedScrollPanel _list = new();
    private readonly Panel _canvas = new() { BackColor = Theme.BackgroundColor };
    private readonly ToolTip _tips = new() { InitialDelay = 450, AutoPopDelay = 15000, ShowAlways = true };
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _stateTimer = new() { Interval = 120 };
    private readonly SemaphoreSlim _thumbnailGate = new(4);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Image?> _thumbnails = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _busy = [];
    private readonly List<ModCard> _cards = [];

    private List<NexusMod> _mods = [];
    private List<NexusMod> _view = [];
    private DateTimeOffset? _fetchedAt;
    private WheelFilter? _wheelFilter;
    private string _lastError = "";
    private bool _loading, _viewDirty, _thumbnailErrorReported, _syncingColumns;
    private int _shown, _loadedCount, _updateCount, _cardWidth = 326, _cardHeight = 158, _columns = 1, _effectiveColumns = 1;

    // ---- What the owner plugs in
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string CachePath { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int, ModInstallInfo?> GetInstallInfo { get; set; } = _ => null;
    /// <summary>Downloads and installs (or updates) a mod. Reports short progress texts such as "Downloading 42%".</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<NexusMod, bool, IProgress<string>, Task>? InstallAsync { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<NexusMod>? Uninstall { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<string>? OpenUrl { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<string>? ReportStatus { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<string, Exception>? ReportError { get; set; }
    /// <summary>Called for every card that is created, so the owner can wire up things such as file drops.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<Control>? PrepareControl { get; set; }

    /// <summary>Raised when the number of mods with an available update changes.</summary>
    public event EventHandler? UpdateCountChanged;

    public int UpdateCount => _updateCount;

    /// <summary>Raised when a new mod list was loaded (from the cache or from Nexus).</summary>
    public event EventHandler? CatalogChanged;

    public IReadOnlyList<NexusMod> Mods => _mods;

    /// <summary>Raised when the user picks a different number of columns.</summary>
    public event EventHandler? ColumnsChanged;

    /// <summary>How many columns of mods to show (1 to <see cref="MaxColumns"/>). Fewer are used when the window is too narrow for them.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Columns
    {
        get => _columns;
        set => SetColumns(value, raise: false);
    }
    public int ModCount => _mods.Count;
    public bool HasMod(int modId) => _mods.Any(mod => mod.ModId == modId);

    public ModBrowserPanel(NexusClient client)
    {
        _client = client;
        DoubleBuffered = true;
        BackColor = Theme.BackgroundColor;

        _sort = CreateCombo(() => ApplyView(), "Recently updated", "Most endorsed", "Most downloaded", "Newest", "Name (A-Z)");
        _filter = CreateCombo(() => ApplyView(), "All mods", "Installed", "Updates available");
        _columnsCombo = CreateCombo(() =>
        {
            if (!_syncingColumns) SetColumns(_columnsCombo!.SelectedIndex + 1, raise: true);
        }, [.. Enumerable.Range(1, MaxColumns).Select(count => count == 1 ? "1 column" : $"{count} columns")]);
        _tips.SetToolTip(_columnsCombo, "How many columns of mods to show");
        _refreshButton = CreateHeaderButton("Refresh", 76, () => _ = RefreshCatalogAsync());
        _moreButton = Theme.CreateButton("Show more", ShowMore, _cardWidth);
        _moreButton.Visible = false;
        _searchHost = Theme.CreateTextFieldHost(_search);
        _tips.SetToolTip(_refreshButton, "Download the mod list from Nexus Mods again");

        _canvas.Width = _cardWidth + CardMargin * 2;
        _canvas.Controls.Add(_moreButton);
        _list.Controls.Add(_canvas);
        Controls.AddRange([_title, _refreshButton, _summary, _searchHost, _sort, _filter, _columnsCombo, _list, _message]);
        _message.BringToFront();

        Resize += (_, _) => LayoutContent();
        _list.Scroll += (_, _) => CheckLoadMore();
        _search.TextChanged += (_, _) =>
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyView();
        };
        _stateTimer.Tick += (_, _) =>
        {
            _stateTimer.Stop();
            ApplyInstallStates();
        };
        LayoutContent();
    }

    // ---- Loading the catalog

    /// <summary>Shows the cached list straight away, then refreshes it from Nexus when it is stale.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            if (NexusCatalogFile.TryLoad(CachePath, out var cached) && cached.Mods.Count > 0)
            {
                _mods = cached.Mods;
                _fetchedAt = cached.FetchedAt;
                CatalogChanged?.Invoke(this, EventArgs.Empty);
                ApplyView();
            }
            if (_fetchedAt is null || DateTimeOffset.UtcNow - _fetchedAt > CacheMaxAge || _mods.All(mod => mod.FileSizeKb == 0)) await RefreshCatalogAsync(); // a list saved by an older version has no file sizes
        }
        catch (Exception ex) { ReportStatus?.Invoke($"Could not start the mod browser: {ex.Message}"); }
    }

    public async Task RefreshCatalogAsync()
    {
        if (_loading || IsDisposed) return;
        _loading = true;
        _loadedCount = 0;
        _lastError = "";
        UpdateSummary();
        UpdateMessage();
        try
        {
            var mods = await _client.FetchModsAsync(new Progress<int>(count =>
            {
                _loadedCount = count;
                UpdateSummary();
            }), _lifetime.Token);
            if (mods.Count == 0 && _mods.Count > 0) throw new NexusException("Nexus Mods returned an empty mod list, so the previous one was kept.");
            _mods = mods;
            CatalogChanged?.Invoke(this, EventArgs.Empty);
            _fetchedAt = DateTimeOffset.UtcNow;
            if (CachePath != "") new NexusCatalogFile(_fetchedAt.Value, mods).Save(CachePath);
            ReportStatus?.Invoke($"Loaded {mods.Count} mod(s) from Nexus Mods.");
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            ReportStatus?.Invoke($"Could not refresh the mod browser: {ex.Message}");
        }
        finally { _loading = false; }
        if (IsDisposed) return;
        ApplyView(keepScroll: true);
        UpdateSummary();
    }

    /// <summary>Call when mods were installed, removed or reordered; cheap enough to call often.</summary>
    public void RefreshInstallStates()
    {
        if (IsDisposed) return;
        _stateTimer.Stop();
        _stateTimer.Start();
    }

    // ---- Filtering, sorting and building the list

    private void ApplyView(bool keepScroll = false)
    {
        IEnumerable<NexusMod> query = _mods;
        var text = _search.Text.Trim();
        if (text != "")
            query = query.Where(mod => mod.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || mod.Author.Contains(text, StringComparison.OrdinalIgnoreCase) || mod.Summary.Contains(text, StringComparison.OrdinalIgnoreCase));
        query = _filter.SelectedIndex switch
        {
            1 => query.Where(mod => GetInstallInfo(mod.ModId) is not null),
            2 => query.Where(IsUpdateAvailable),
            _ => query
        };
        query = _sort.SelectedIndex switch
        {
            1 => query.OrderByDescending(mod => mod.Endorsements),
            2 => query.OrderByDescending(mod => mod.Downloads),
            3 => query.OrderByDescending(mod => mod.CreatedAt),
            4 => query.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderByDescending(mod => mod.UpdatedAt)
        };

        var scroll = keepScroll ? -_list.AutoScrollPosition.Y : 0;
        var keepShown = keepScroll ? _shown : 0;
        _view = query.ToList();
        _shown = Math.Min(_view.Count, Math.Max(PageSize, keepShown));
        if (!Visible) _viewDirty = true; // built when the window is shown
        else
        {
            Rebuild();
            if (scroll > 0) _list.AutoScrollPosition = new Point(0, scroll);
        }
        UpdateMessage();
        UpdateSummary();
    }

    private void Rebuild()
    {
        _viewDirty = false;
        _list.SuspendLayout();
        try
        {
            _list.AutoScrollPosition = Point.Empty;
            foreach (var card in _cards) card.Dispose();
            _cards.Clear();
            AppendCards(0, _shown);
        }
        finally { _list.ResumeLayout(true); }
    }

    private Point PositionOf(int index) =>
        new(CardMargin + index % _effectiveColumns * (_cardWidth + CardGap), CardMargin + index / _effectiveColumns * (_cardHeight + CardGap));

    private void AppendCards(int from, int to)
    {
        for (var index = from; index < to; index++)
        {
            var card = new ModCard(this, _view[index], _cardWidth) { Location = PositionOf(index) };
            card.Apply(GetInstallInfo(card.Mod.ModId), _busy.GetValueOrDefault(card.Mod.ModId), _busy.Count > 0);
            _canvas.Controls.Add(card);
            _cards.Add(card);
            PrepareControl?.Invoke(card);
            LoadThumbnail(card);
        }
        LayoutBelowCards();
    }

    /// <summary>Places the "Show more" button under the last row and sizes the scrollable area.</summary>
    private void LayoutBelowCards()
    {
        var rows = (_cards.Count + _effectiveColumns - 1) / _effectiveColumns;
        var y = CardMargin + rows * (_cardHeight + CardGap);
        _moreButton.Visible = _shown < _view.Count;
        if (_moreButton.Visible)
        {
            _moreButton.SetBounds(CardMargin, y, Math.Max(1, _canvas.Width - CardMargin * 2), 30);
            _moreButton.Text = $"Show more ({_view.Count - _shown} left)";
            y += _moreButton.Height + CardGap;
        }
        _canvas.Height = Math.Max(1, y);
    }

    private void SetColumns(int columns, bool raise)
    {
        columns = Math.Clamp(columns, 1, MaxColumns);
        if (_syncingColumns) return;
        _syncingColumns = true;
        try { _columnsCombo.SelectedIndex = columns - 1; }
        finally { _syncingColumns = false; }
        if (columns == _columns) return;
        _columns = columns;
        UpdateCardGeometry();
        if (raise) ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Works out card size and column count for the current width and moves the cards accordingly.</summary>
    private void UpdateCardGeometry()
    {
        var width = ClientSize.Width;
        if (width < 120) return;
        // Room for the scrollbar is always reserved so cards never jump when it appears.
        var available = width - SystemInformation.VerticalScrollBarWidth - CardMargin * 2;
        var columns = Math.Clamp((available + CardGap) / (MinCardWidth + CardGap), 1, _columns);
        var cardWidth = Math.Max(MinCardWidth, (available - CardGap * (columns - 1)) / columns);
        if (columns == _effectiveColumns && cardWidth == _cardWidth) return;

        var scroll = -_list.AutoScrollPosition.Y;
        _effectiveColumns = columns;
        _cardWidth = cardWidth;
        _cardHeight = CardHeightFor(cardWidth);
        _list.SuspendLayout();
        _canvas.Width = available + CardMargin * 2;
        for (var index = 0; index < _cards.Count; index++)
        {
            _cards[index].SetWidth(cardWidth);
            _cards[index].Location = PositionOf(index);
        }
        LayoutBelowCards();
        _list.ResumeLayout(true);
        if (scroll > 0) _list.AutoScrollPosition = new Point(0, scroll);
    }

    private void ShowMore()
    {
        if (_shown >= _view.Count) return;
        var from = _shown;
        _shown = Math.Min(_view.Count, _shown + PageSize);
        AppendCards(from, _shown);
    }

    private void CheckLoadMore()
    {
        if (_shown < _view.Count && -_list.AutoScrollPosition.Y + _list.ClientSize.Height >= _canvas.Height - 260) ShowMore();
    }

    private void ScrollBy(int pixels)
    {
        _list.AutoScrollPosition = new Point(0, Math.Max(0, -_list.AutoScrollPosition.Y + pixels));
        CheckLoadMore();
    }

    private void UpdateMessage()
    {
        if (_view.Count > 0)
        {
            _message.Visible = false;
            return;
        }
        _message.Text = _mods.Count > 0 ? "No mods match your search or filter."
            : _loading ? "Loading mods from Nexus Mods..."
            : _lastError != "" ? $"Could not load the mod list.\r\n\r\n{_lastError}\r\n\r\nPress Refresh to try again."
            : "No mods found.";
        _message.Visible = true;
    }

    private bool IsUpdateAvailable(NexusMod mod) => GetInstallInfo(mod.ModId) is { } info && NexusVersion.IsNewer(mod.Version, info.Version);

    private void UpdateSummary()
    {
        var updates = _mods.Count(IsUpdateAvailable);
        var updatesChanged = updates != _updateCount;
        _updateCount = updates;
        _summary.Text = _loading ? (_loadedCount > 0 ? $"Loading mods... {_loadedCount}" : "Loading mods from Nexus Mods...")
            : _mods.Count == 0 ? "Nexus Mods" + Dot + "Redout 2"
            : $"Nexus Mods{Dot}{_mods.Count} mods" + (_updateCount > 0 ? $"{Dot}{_updateCount} update{(_updateCount == 1 ? "" : "s")}" : "") + (_lastError != "" ? Dot + "offline" : "");
        _tips.SetToolTip(_summary, _lastError != "" ? $"The last refresh failed: {_lastError}" : _fetchedAt is { } fetched ? $"Mod list downloaded {fetched.ToLocalTime():yyyy-MM-dd HH:mm}" : "");
        if (updatesChanged) UpdateCountChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Installed or removed mods change the cards (and, with a filter, which cards exist).</summary>
    private void ApplyInstallStates()
    {
        if (_filter.SelectedIndex != 0 && _busy.Count == 0 && Visible) ApplyView(keepScroll: true);
        else
        {
            UpdateAllCards();
            UpdateSummary();
        }
    }

    private void UpdateAllCards()
    {
        var anyBusy = _busy.Count > 0;
        foreach (var card in _cards) card.Apply(GetInstallInfo(card.Mod.ModId), _busy.GetValueOrDefault(card.Mod.ModId), anyBusy);
    }

    // ---- Card actions

    private async void OnAction(ModCard card)
    {
        var mod = card.Mod;
        var isUpdate = card.IsUpdate;
        if (_busy.Count > 0 || InstallAsync is null) return;
        SetBusy(mod.ModId, isUpdate ? "Updating..." : "Starting...");
        try
        {
            await InstallAsync(mod, isUpdate, new Progress<string>(text =>
            {
                if (_busy.ContainsKey(mod.ModId)) SetBusy(mod.ModId, text);
            }));
        }
        catch (Exception ex) { ReportError?.Invoke($"Could not {(isUpdate ? "update" : "download")} {mod.Name}.", ex); }
        finally
        {
            _busy.Remove(mod.ModId);
            if (!IsDisposed) ApplyInstallStates();
        }
    }

    private void OnRemove(ModCard card)
    {
        if (_busy.Count > 0) return;
        try { Uninstall?.Invoke(card.Mod); }
        catch (Exception ex) { ReportError?.Invoke($"Could not uninstall {card.Mod.Name}.", ex); }
        ApplyInstallStates();
    }

    private void SetBusy(int modId, string text)
    {
        _busy[modId] = text;
        UpdateAllCards();
    }

    // ---- Thumbnails

    private async void LoadThumbnail(ModCard card)
    {
        var url = card.Mod.ThumbnailUrl;
        if (url == "") return;
        try
        {
            if (!_thumbnails.TryGetValue(url, out var image))
            {
                image = await FetchThumbnailAsync(url);
                _thumbnails[url] = image;
            }
            if (image is not null && !card.IsDisposed) card.SetThumbnail(image);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _thumbnails[url] = null; // the placeholder stays; the reason is reported once
            if (_thumbnailErrorReported) return;
            _thumbnailErrorReported = true;
            ReportStatus?.Invoke($"Could not load a mod thumbnail ({url}): {ex.Message}");
        }
    }

    private async Task<Image?> FetchThumbnailAsync(string url)
    {
        await _thumbnailGate.WaitAsync(_lifetime.Token);
        try
        {
            var bytes = await _client.GetBytesAsync(url, 4 * 1024 * 1024, _lifetime.Token);
            return ThumbnailDecoder.Decode(bytes);
        }
        finally { _thumbnailGate.Release(); }
    }

    // ---- Layout and painting

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && _viewDirty) Rebuild();
    }

    private void LayoutContent()
    {
        var width = ClientSize.Width;
        var height = ClientSize.Height;
        if (width < 120) return;
        const int pad = 12, gap = 8, columnsWidth = 96;
        // Search box and drop-downs share one row height (a drop-down is as tall as its font needs).
        var row = Math.Max(_sort.PreferredHeight, 26);
        var searchTop = 66;
        var comboTop = searchTop + row + gap;
        var listTop = comboTop + row + 12;

        _refreshButton.SetBounds(width - pad - 76, 10, 76, 28);
        _title.SetBounds(pad, 10, Math.Max(0, _refreshButton.Left - pad - 6), 28);
        _summary.SetBounds(pad, 42, width - pad * 2, 18);
        _searchHost.SetBounds(pad, searchTop, width - pad * 2, row);

        var rest = width - pad * 2 - columnsWidth - gap * 2;
        var sortWidth = rest * 55 / 100;
        var filterWidth = rest - sortWidth;
        PlaceCombo(_sort, pad, sortWidth);
        PlaceCombo(_filter, pad + sortWidth + gap, filterWidth);
        PlaceCombo(_columnsCombo, pad + sortWidth + gap + filterWidth + gap, columnsWidth);
        void PlaceCombo(ComboBox combo, int x, int comboWidth)
        {
            combo.SetBounds(x, comboTop, comboWidth, row);
            combo.Top = comboTop + (row - combo.Height) / 2;
        }

        _list.SetBounds(0, listTop, width, Math.Max(0, height - listTop));
        _message.SetBounds(pad, listTop + 24, width - pad * 2, 140);
        UpdateCardGeometry();
    }

    // ---- Control factories

    private ComboBox CreateCombo(Action onChanged, params string[] items)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.PanelColor, ForeColor = Theme.TextColor, Font = HeaderFont };
        combo.Items.AddRange(items);
        combo.SelectedIndex = 0;
        combo.SelectedIndexChanged += (_, _) => onChanged();
        return combo;
    }

    private static Button CreateHeaderButton(string text, int width, Action onClick)
    {
        var button = Theme.CreateButton(text, onClick, width);
        button.Size = new Size(width, 28);
        button.Font = HeaderFont;
        return button;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_wheelFilter is not null) return;
        _wheelFilter = new WheelFilter(this);
        Application.AddMessageFilter(_wheelFilter);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        RemoveWheelFilter();
        base.OnHandleDestroyed(e);
    }

    private void RemoveWheelFilter()
    {
        if (_wheelFilter is null) return;
        Application.RemoveMessageFilter(_wheelFilter);
        _wheelFilter = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifetime.Cancel();
            RemoveWheelFilter();
            _searchTimer.Dispose();
            _stateTimer.Dispose();
            _tips.Dispose();
            foreach (var image in _thumbnails.Values) image?.Dispose();
            _thumbnails.Clear();
        }
        base.Dispose(disposing);
    }

    // ---- Helpers

    /// <summary>12 345 becomes "12.3K", 2 500 000 becomes "2.5M"; small numbers stay as they are.</summary>
    private static string CompactCount(long count) => count switch
    {
        >= 1_000_000 => $"{count / 1_000_000.0:0.#}M",
        >= 10_000 => $"{count / 1_000.0:0.#}K",
        _ => count.ToString("N0")
    };

    private static string FileSizeText(long kilobytes) => kilobytes switch
    {
        <= 0 => "Size unknown",
        < 1024 => $"{kilobytes} KB",
        < 1024 * 1024 => $"{kilobytes / 1024.0:0.#} MB",
        _ => $"{kilobytes / (1024.0 * 1024.0):0.##} GB"
    };

    private static string VersionText(string version) => version == "" ? "" : char.IsDigit(version[0]) ? "v" + version : version;

    /// <summary>Scrolls the list when the wheel turns over it, without stealing keyboard focus from the search box.</summary>
    private sealed class WheelFilter(ModBrowserPanel owner) : IMessageFilter
    {
        private const int WmMouseWheel = 0x020A;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WmMouseWheel || !owner.IsHandleCreated || !owner.Visible) return false;
            var target = Control.FromHandle(m.HWnd);
            if (target is null || (target != owner._list && !owner._list.Contains(target))) return false;
            var delta = unchecked((short)((long)m.WParam >> 16));
            owner.ScrollBy(-delta * 96 / 120);
            return true;
        }
    }

    /// <summary>A scrolling panel that does not flicker.</summary>
    private sealed class BufferedScrollPanel : Panel
    {
        public BufferedScrollPanel()
        {
            DoubleBuffered = true;
            AutoScroll = true;
            BackColor = Theme.BackgroundColor;
            Theme.UseDarkScrollbars(this);
        }

        // Pressing a card's button focuses it, and by default the panel then scrolls to bring the focused control into view.
        // That made the list jump when Download was clicked, so the scroll position is simply kept.
        protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;
    }

    /// <summary>One mod: thumbnail, name, creator, version, install state, summary and its buttons.</summary>
    private sealed class ModCard : Panel
    {
        private readonly Label _status, _meta, _name, _summary, _downloads, _endorsements, _size;
        private readonly PictureBox _thumbnail;
        private readonly Button _action, _remove;
        private bool _wide, _hot;
        private int _buttonsTop;

        public NexusMod Mod { get; }
        public bool IsUpdate { get; private set; }

        public ModCard(ModBrowserPanel owner, NexusMod mod, int width)
        {
            Mod = mod;
            DoubleBuffered = true;
            BackColor = Theme.PanelColor;

            _thumbnail = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.BackgroundColor };
            _name = CreateLabel(mod.Name, NameFont, Theme.TextColor);
            _meta = CreateLabel($"by {mod.Author}" + (mod.Version == "" ? "" : Dot + VersionText(mod.Version)), SmallFont, Theme.MutedColor);
            _status = CreateLabel("", SmallFont, Theme.MutedColor);
            _summary = CreateLabel(mod.Summary, SmallFont, Theme.MutedColor);
            _downloads = CreateLabel($"Downloads: {CompactCount(mod.Downloads)}", SmallFont, Theme.TextColor);
            _endorsements = CreateLabel($"Likes: {CompactCount(mod.Endorsements)}", SmallFont, Theme.TextColor);
            _size = CreateLabel($"File size: {FileSizeText(mod.FileSizeKb)}", SmallFont, Theme.TextColor);
            _action = CreateButton("Download", () => owner.OnAction(this));
            _remove = CreateButton("Uninstall", () => owner.OnRemove(this));
            Controls.AddRange([_thumbnail, _name, _meta, _status, _downloads, _endorsements, _size, _summary, _action, _remove]);
            Relayout(width);

            // Clicking the mod itself opens its Nexus Mods page.
            var tip = $"{mod.Name}\n{mod.Summary}\n\nLikes: {mod.Endorsements:N0}\nDownloads: {mod.Downloads:N0}\nFile size: {FileSizeText(mod.FileSizeKb)}\nClick to open on Nexus Mods";
            foreach (var control in new Control[] { this, _thumbnail, _name, _meta, _status, _downloads, _endorsements, _size, _summary })
            {
                control.Cursor = Cursors.Hand;
                control.Click += (_, _) => owner.OpenUrl?.Invoke(mod.PageUrl);
                owner._tips.SetToolTip(control, tip);
            }

            // The card lights up while the mouse is anywhere over it, including its labels and buttons.
            foreach (var control in new Control[] { this, _thumbnail, _name, _meta, _status, _downloads, _endorsements, _size, _summary, _action, _remove })
            {
                control.MouseEnter += (_, _) => UpdateHot();
                control.MouseLeave += (_, _) => UpdateHot();
            }
        }

        private void UpdateHot()
        {
            var hot = IsHandleCreated && ClientRectangle.Contains(PointToClient(Cursor.Position));
            if (hot == _hot) return;
            _hot = hot;
            BackColor = hot ? Theme.PanelHoverColor : Theme.PanelColor; // the labels take this over from the card
            Invalidate();
        }

        public void SetThumbnail(Image image) => _thumbnail.Image = image;

        public void SetWidth(int width)
        {
            Relayout(width);
            Invalidate();
        }

        /// <summary>Lays the card out for its width: wide cards put the thumbnail beside the text, narrow ones stack it on top.</summary>
        private void Relayout(int width)
        {
            _wide = IsWide(width);
            Size = new Size(width, CardHeightFor(width));
            if (_wide)
            {
                _thumbnail.Bounds = new Rectangle(8, 8, 96, 54);
                _name.Bounds = new Rectangle(112, 6, width - 120, 20);
                _meta.Bounds = new Rectangle(112, 28, width - 120, 16);
                _status.Bounds = new Rectangle(112, 46, width - 120, 16);
                // Downloads, endorsements and file size sit side by side.
                var inner = width - 16;
                var likesWidth = inner * 26 / 100;
                var downloadsWidth = inner * 36 / 100;
                _endorsements.Bounds = new Rectangle(8, 68, likesWidth, 16);
                _downloads.Bounds = new Rectangle(8 + likesWidth, 68, downloadsWidth, 16);
                _size.Bounds = new Rectangle(8 + likesWidth + downloadsWidth, 68, inner - likesWidth - downloadsWidth, 16);
                _summary.Bounds = new Rectangle(8, 90, width - 16, 30);
                _buttonsTop = 124;
            }
            else
            {
                var thumbnailHeight = ThumbnailHeight(width);
                var top = 8 + thumbnailHeight + 6;
                _thumbnail.Bounds = new Rectangle(8, 8, width - 16, thumbnailHeight);
                _name.Bounds = new Rectangle(8, top, width - 16, 20);
                _meta.Bounds = new Rectangle(8, top + 22, width - 16, 16);
                _status.Bounds = new Rectangle(8, top + 40, width - 16, 16);
                // Narrow cards stack the three figures.
                _endorsements.Bounds = new Rectangle(8, top + 60, width - 16, 16);
                _downloads.Bounds = new Rectangle(8, top + 76, width - 16, 16);
                _size.Bounds = new Rectangle(8, top + 92, width - 16, 16);
                _summary.Bounds = new Rectangle(8, top + 114, width - 16, 30);
                _buttonsTop = top + 150;
            }
            LayoutButtons();
        }

        private void LayoutButtons()
        {
            if (_wide)
            {
                _action.SetBounds(8, _buttonsTop, 108, 26);
                _remove.SetBounds(_action.Visible ? 124 : 8, _buttonsTop, 92, 26);
                return;
            }
            // Narrow cards: the visible buttons share the full width.
            var full = Width - 16;
            var each = _action.Visible && _remove.Visible ? (full - 6) / 2 : full;
            _action.SetBounds(8, _buttonsTop, each, 26);
            _remove.SetBounds(_action.Visible ? 8 + each + 6 : 8, _buttonsTop, each, 26);
        }

        /// <param name="info">Null when the mod is not installed.</param>
        /// <param name="busyText">Progress text while this card's mod is being downloaded or installed.</param>
        /// <param name="anotherBusy">Some mod is being installed, so no button may be used.</param>
        public void Apply(ModInstallInfo? info, string? busyText, bool anotherBusy)
        {
            var installed = info is not null;
            // A mod recognised by its name has no known version; it can be reinstalled to link it to the version on Nexus.
            var unknownVersion = installed && info!.Version == "";
            IsUpdate = installed && (unknownVersion || NexusVersion.IsNewer(Mod.Version, info!.Version));

            (_status.Text, _status.ForeColor) = busyText is not null ? (busyText, Theme.LinkColor)
                : !installed ? ($"Updated {Mod.UpdatedAt.ToLocalTime():MMM d, yyyy}", Theme.MutedColor)
                : unknownVersion ? ($"Installed{Dot}version unknown", Theme.SuccessColor)
                : IsUpdate ? ($"Installed {VersionText(info!.Version)}{Dot}update to {VersionText(Mod.Version)}", Theme.HighlightColor)
                : ($"Installed{Dot}{VersionText(info!.Version)}", Theme.SuccessColor);

            _action.Visible = !installed || IsUpdate;
            _action.Text = unknownVersion ? "Reinstall" : IsUpdate ? "Update" : "Download";
            Theme.StyleButton(_action, unknownVersion ? ButtonKind.Normal : IsUpdate ? ButtonKind.Attention : ButtonKind.Primary);
            _remove.Visible = installed;
            LayoutButtons();
            _action.Enabled = _remove.Enabled = busyText is null && !anotherBusy;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(_hot ? Theme.AccentHoverColor : Theme.BorderColor);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        private static Label CreateLabel(string text, Font font, Color color) =>
            new() { Text = text, Font = font, ForeColor = color, AutoEllipsis = true, UseMnemonic = false };

        private static Button CreateButton(string text, Action onClick)
        {
            var button = Theme.CreateButton(text, onClick, 100);
            button.Font = SmallFont;
            return button;
        }
    }
}

/// <summary>The window that hosts the mod browser. Closing it only hides it, so the list, scroll position and thumbnails stay.</summary>
internal sealed class ModBrowserWindow : Form
{
    public ModBrowserWindow(ModBrowserPanel panel, Icon? icon)
    {
        Text = "Mod Browser - Redout 2";
        Icon = icon;
        BackColor = Theme.BackgroundColor;
        ForeColor = Theme.TextColor;
        Font = Theme.CreateFont(9);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(420, 520);
        Size = new Size(460, 780);
        panel.Dock = DockStyle.Fill;
        Controls.Add(panel);
        panel.ColumnsChanged += (_, _) => FitToColumns(panel.Columns);
    }

    /// <summary>Widens the window, if needed, so that the chosen number of columns fits.</summary>
    public void FitToColumns(int columns)
    {
        var extra = ModBrowserPanel.RequiredClientWidth(columns) - ClientSize.Width;
        if (extra <= 0 || WindowState != FormWindowState.Normal) return;
        var area = Screen.FromControl(this).WorkingArea;
        Width = Math.Min(area.Width, Width + extra);
        if (Right > area.Right) Left = Math.Max(area.Left, area.Right - Width);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }
}

// Everything that talks to Nexus Mods.
//
//  - Browsing (title, summary, author, version, thumbnail, update time) uses the public GraphQL v2 API and needs no key.
//  - File lists and download links use the v1 REST API and need the user's personal API key.
//    Nexus only hands out direct download links to Premium accounts; free accounts must use the website.

internal sealed record NexusMod(
    int ModId,
    string Name,
    string Summary,
    string Version,
    string Author,
    string ThumbnailUrl,
    DateTimeOffset UpdatedAt,
    DateTimeOffset CreatedAt,
    long Downloads,
    long Endorsements,
    long FileSizeKb = 0) // size of the primary file; 0 when unknown (catalogs saved by older versions)
{
    [JsonIgnore] public string PageUrl => NexusClient.ModPageUrl(ModId);
}

internal sealed record NexusFile(int FileId, string FileName, string Category, bool IsPrimary, long UploadedTimestamp);

internal sealed record NexusUser(string Name, bool IsPremium);

/// <summary>Links a Nexus mod to the PAK files it installed. PAK names are stored without the NNN_ load-order prefix.</summary>
internal sealed class NexusLink
{
    public int ModId { get; set; }
    public string Version { get; set; } = "";
    public List<string> PakFiles { get; set; } = [];
}

internal class NexusException(string message) : Exception(message);

/// <summary>The API key is missing or was rejected.</summary>
internal sealed class NexusApiKeyException(string message) : NexusException(message);

/// <summary>Nexus only gives direct download links to Premium members.</summary>
internal sealed class NexusPremiumRequiredException()
    : NexusException("Nexus Mods only allows direct downloads for Premium members.");

internal sealed class NexusClient : IDisposable
{
    public const string GameDomain = "redout2";

    private const string GraphQlUrl = "https://api.nexusmods.com/v2/graphql";
    private const string RestBase = "https://api.nexusmods.com/v1";
    private const int PageSize = 30;
    private const int MaxPages = 100;
    private const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private const string ModsQuery = """
        query Mods($offset: Int, $count: Int) {
          mods(
            filter: { gameDomainName: [{ value: "redout2", op: EQUALS }] }
            sort: [{ updatedAt: { direction: DESC } }]
            offset: $offset
            count: $count
          ) {
            nodes {
              modId name summary version author thumbnailUrl pictureUrl updatedAt createdAt downloads endorsements fileSize
            }
          }
        }
        """;

    private readonly HttpClient _http;

    /// <summary>The user's personal API key. Only ever sent to the v1 REST API, never to GraphQL or the CDN.</summary>
    public string ApiKey { get; set; } = "";

    public static string ModPageUrl(int modId) => $"https://www.nexusmods.com/{GameDomain}/mods/{modId}";
    public static string FilesPageUrl(int modId) => ModPageUrl(modId) + "?tab=files";

    public NexusClient(string appVersion)
    {
        _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(15) })
        {
            Timeout = Timeout.InfiniteTimeSpan // per-call timeouts are applied below; downloads only time out when they stall
        };
        // Nexus asks API clients to identify themselves.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"R2MM/{appVersion}");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("(Redout 2 Mod Manager; Windows)");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Application-Name", "R2MM");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Application-Version", appVersion);
    }

    public void Dispose() => _http.Dispose();

    // ---- Catalog (GraphQL v2, no key needed)

    /// <summary>Downloads every Redout 2 mod, page by page. Returns them newest-updated first.</summary>
    public async Task<List<NexusMod>> FetchModsAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var mods = new List<NexusMod>();
        var seen = new HashSet<int>();
        var offset = 0;
        for (var page = 0; page < MaxPages; page++)
        {
            var body = JsonSerializer.Serialize(new { query = ModsQuery, variables = new { offset, count = PageSize } });
            using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var document = await SendForJsonAsync(request, isDownloadLink: false, cancellationToken).ConfigureAwait(false);

            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("mods", out var connection) || connection.ValueKind != JsonValueKind.Object
                || !connection.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                throw new NexusException(DescribeGraphQlError(root));

            var received = nodes.GetArrayLength();
            if (received == 0) break;
            offset += received;

            var added = 0;
            foreach (var node in nodes.EnumerateArray())
            {
                if (ParseMod(node) is not { } mod || !seen.Add(mod.ModId)) continue;
                mods.Add(mod);
                added++;
            }
            progress?.Report(mods.Count);
            if (added == 0) break; // the server ignored the offset; stop instead of looping forever
        }
        return mods;
    }

    private static NexusMod? ParseMod(JsonElement node)
    {
        var id = (int)Number(node, "modId");
        var name = NexusText.Clean(Text(node, "name"));
        if (id <= 0 || name == "") return null; // moderated or removed mods come back without a name
        var thumbnail = Text(node, "thumbnailUrl");
        var picture = Text(node, "pictureUrl");
        var author = NexusText.Clean(Text(node, "author"));
        return new NexusMod(id, name, NexusText.Clean(Text(node, "summary")), Text(node, "version").Trim(),
            author == "" ? "Unknown" : author, thumbnail != "" ? thumbnail : picture,
            Date(node, "updatedAt"), Date(node, "createdAt"), Number(node, "downloads"), Number(node, "endorsements"), Number(node, "fileSize"));
    }

    private static string DescribeGraphQlError(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var message = Text(errors[0], "message");
            if (message != "") return "Nexus Mods rejected the mod list request: " + (message.Length > 300 ? message[..300] + "..." : message);
        }
        return "Nexus Mods returned an unexpected answer to the mod list request.";
    }

    // ---- Files and downloads (REST v1, needs the API key)

    public async Task<NexusUser> ValidateKeyAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync($"{RestBase}/users/validate.json", withKey: true, isDownloadLink: false, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return new NexusUser(Text(root, "name"), root.TryGetProperty("is_premium", out var premium) && premium.ValueKind == JsonValueKind.True);
    }

    public async Task<List<NexusFile>> GetFilesAsync(int modId, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync($"{RestBase}/games/{GameDomain}/mods/{modId}/files.json", withKey: true, isDownloadLink: false, cancellationToken).ConfigureAwait(false);
        var files = new List<NexusFile>();
        if (document.RootElement.TryGetProperty("files", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
            {
                var id = (int)Number(item, "file_id");
                if (id <= 0) continue;
                files.Add(new NexusFile(id, Text(item, "file_name"), Text(item, "category_name").ToUpperInvariant(),
                    item.TryGetProperty("is_primary", out var primary) && primary.ValueKind == JsonValueKind.True, Number(item, "uploaded_timestamp")));
            }
        return files;
    }

    /// <summary>The file the mod page's own Download button would give: the primary file, else the newest MAIN file, else the newest current file.</summary>
    public static NexusFile? PickMainFile(IEnumerable<NexusFile> files) => files
        .Where(file => file.Category is not ("OLD_VERSION" or "ARCHIVED" or "DELETED"))
        .OrderByDescending(file => file.IsPrimary)
        .ThenByDescending(file => file.Category == "MAIN")
        .ThenByDescending(file => file.UploadedTimestamp)
        .FirstOrDefault();

    /// <summary>Short-lived CDN link. Throws <see cref="NexusPremiumRequiredException"/> for free accounts.</summary>
    public async Task<string> GetDownloadUrlAsync(int modId, int fileId, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync($"{RestBase}/games/{GameDomain}/mods/{modId}/files/{fileId}/download_link.json", withKey: true, isDownloadLink: true, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var mirror in document.RootElement.EnumerateArray())
                if (Text(mirror, "URI") is { Length: > 0 } uri) return uri;
        throw new NexusException("Nexus Mods did not return a download link for this file.");
    }

    public async Task DownloadFileAsync(string url, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        RequireNexusUrl(url);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        var partialPath = destinationPath + ".part";
        try
        {
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headerTimeout.CancelAfter(ApiTimeout);
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new NexusException($"The download failed (HTTP {(int)response.StatusCode}).");

            var total = response.Content.Headers.ContentLength ?? 0;
            if (total > MaxDownloadBytes) throw new NexusException("The download is larger than R2MM allows.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var output = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                var lastPercent = -1;
                while (true)
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    stall.CancelAfter(StallTimeout);
                    var read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    received += read;
                    if (received > MaxDownloadBytes) throw new NexusException("The download is larger than R2MM allows.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (total <= 0) continue;
                    var percent = (int)(received * 100 / total);
                    if (percent == lastPercent) continue;
                    lastPercent = percent;
                    progress?.Report(percent / 100.0);
                }
            }
            File.Move(partialPath, destinationPath, overwrite: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryDelete(partialPath);
            throw new NexusException("The download stalled and was cancelled.");
        }
        catch (HttpRequestException ex)
        {
            TryDelete(partialPath);
            throw new NexusException("Could not download the file: " + ex.Message);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    /// <summary>Small downloads such as thumbnails. Only Nexus hosts are contacted.</summary>
    public async Task<byte[]> GetBytesAsync(string url, int maxBytes, CancellationToken cancellationToken = default)
    {
        RequireNexusUrl(url);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ApiTimeout);
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new NexusException($"HTTP {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > maxBytes) throw new NexusException("The file is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > maxBytes) throw new NexusException("The file is too large.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new NexusException("Nexus Mods did not respond in time."); }
        catch (HttpRequestException ex) { throw new NexusException("Could not reach Nexus Mods: " + ex.Message); }
    }

    // ---- HTTP plumbing

    private Task<JsonDocument> GetJsonAsync(string url, bool withKey, bool isDownloadLink, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (withKey)
        {
            if (string.IsNullOrWhiteSpace(ApiKey)) { request.Dispose(); throw new NexusApiKeyException("No Nexus Mods API key is set. Add one in Options."); }
            request.Headers.TryAddWithoutValidation("apikey", ApiKey.Trim());
        }
        return SendForJsonAsync(request, isDownloadLink, cancellationToken, disposeRequest: true);
    }

    private async Task<JsonDocument> SendForJsonAsync(HttpRequestMessage request, bool isDownloadLink, CancellationToken cancellationToken, bool disposeRequest = false)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ApiTimeout);
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            ThrowIfFailed(response.StatusCode, isDownloadLink);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new NexusException("Nexus Mods did not respond in time."); }
        catch (HttpRequestException ex) { throw new NexusException("Could not reach Nexus Mods: " + ex.Message); }
        catch (JsonException) { throw new NexusException("Nexus Mods returned an answer R2MM could not read."); }
        finally { if (disposeRequest) request.Dispose(); }
    }

    // Messages never include response bodies or URLs: download links carry credentials.
    private static void ThrowIfFailed(HttpStatusCode status, bool isDownloadLink)
    {
        if ((int)status is >= 200 and < 300) return;
        throw status switch
        {
            HttpStatusCode.Unauthorized => new NexusApiKeyException("Nexus Mods rejected the API key. Check it in Options."),
            HttpStatusCode.Forbidden when isDownloadLink => new NexusPremiumRequiredException(),
            HttpStatusCode.Forbidden => new NexusException("Nexus Mods denied the request (HTTP 403)."),
            HttpStatusCode.NotFound => new NexusException("Nexus Mods could not find that mod or file (HTTP 404)."),
            HttpStatusCode.TooManyRequests => new NexusException("Nexus Mods rate limit reached. Try again later."),
            _ => new NexusException($"Nexus Mods returned HTTP {(int)status}.")
        };
    }

    private static void RequireNexusUrl(string url)
    {
        // Images come from nexusmods.com; premium download links come from Nexus's CDN domain nexus-cdn.com.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !new[] { "nexusmods.com", "nexus-cdn.com" }.Any(domain => uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
            throw new NexusException("Refusing to download from an address that is not a Nexus Mods domain.");
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    // ---- Tolerant JSON readers: a missing or oddly typed field becomes empty, never an exception

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static long Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => 0
        };
    }

    private static DateTimeOffset Date(JsonElement element, string name) =>
        DateTimeOffset.TryParse(Text(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : DateTimeOffset.MinValue;
}

/// <summary>Compares mod versions the way people write them ("1.2", "v1.2.0", "1.2b").</summary>
internal static class NexusVersion
{
    /// <summary>True when <paramref name="latest"/> is a higher version than <paramref name="installed"/>. Unknown versions never count as newer.</summary>
    public static bool IsNewer(string latest, string installed)
    {
        var a = Parts(latest);
        var b = Parts(installed);
        if (a.Count == 0 || b.Count == 0) return false;
        for (var index = 0; index < Math.Max(a.Count, b.Count); index++)
        {
            var (aNumber, aSuffix) = index < a.Count ? a[index] : (0L, "");
            var (bNumber, bSuffix) = index < b.Count ? b[index] : (0L, "");
            if (aNumber != bNumber) return aNumber > bNumber;
            var suffix = string.CompareOrdinal(aSuffix, bSuffix);
            if (suffix != 0) return suffix > 0;
        }
        return false;
    }

    private static List<(long Number, string Suffix)> Parts(string version) =>
        [.. Regex.Split(version.Trim().TrimStart('v', 'V').ToLowerInvariant(), "[^0-9a-z]+")
            .Where(part => part != "")
            .Select(part =>
            {
                var match = Regex.Match(part, @"^(\d*)(.*)$");
                return (long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0L, match.Groups[2].Value);
            })];
}

/// <summary>Reads the file name Nexus gives a downloaded archive: "&lt;name&gt;-&lt;mod id&gt;-&lt;version&gt;-&lt;timestamp&gt;.zip".</summary>
internal static partial class NexusArchiveName
{
    [GeneratedRegex(@"^(?<name>.+?)-(?<id>\d{1,9})-(?<version>\d+(?:-\d+)*[A-Za-z]?)-(?<time>\d{9,11})(?: \(\d+\))?\.(?:zip|7z|rar)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>Best effort: Nexus writes the version with dashes ("1-2-0"), so it is turned back into "1.2.0".</summary>
    public static bool TryParse(string fileName, out int modId, out string version)
    {
        modId = 0;
        version = "";
        var match = Pattern().Match(fileName);
        if (!match.Success || !int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out modId) || modId <= 0) return false;
        version = match.Groups["version"].Value.Replace('-', '.');
        return true;
    }
}

internal static partial class NexusText
{
    /// <summary>Mod summaries are meant to be plain text but sometimes contain HTML entities, tags or line breaks.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var cleaned = LineBreaks().Replace(text, " ");
        cleaned = HtmlTags().Replace(cleaned, "");
        return Whitespace().Replace(WebUtility.HtmlDecode(cleaned), " ").Trim();
    }

    [GeneratedRegex(@"<br\s*/?>|</p>|\r?\n", RegexOptions.IgnoreCase)] private static partial Regex LineBreaks();
    [GeneratedRegex("<[^>]+>")] private static partial Regex HtmlTags();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}

/// <summary>The last mod list, kept on disk so the browser opens instantly and works offline.</summary>
internal sealed record NexusCatalogFile(DateTimeOffset FetchedAt, List<NexusMod> Mods)
{
    public static bool TryLoad(string path, out NexusCatalogFile catalog)
    {
        catalog = new NexusCatalogFile(DateTimeOffset.MinValue, []);
        try
        {
            if (path == "" || !File.Exists(path) || JsonSerializer.Deserialize<NexusCatalogFile>(File.ReadAllText(path)) is not { } loaded) return false;
            catalog = loaded with { Mods = [.. loaded.Mods.Where(mod => mod is { ModId: > 0 } && !string.IsNullOrEmpty(mod.Name))] };
            return true;
        }
        catch { return false; }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch { } // a cache that cannot be written is not worth an error
    }
}

/// <summary>
/// Turns downloaded thumbnail bytes into a small bitmap. Nexus serves many images as WebP, which GDI+ cannot read,
/// so anything GDI+ rejects is decoded with ImageSharp instead.
/// </summary>
internal static class ThumbnailDecoder
{
    private const int MaxWidth = 192;

    private static Size Fit(int width, int height)
    {
        var scale = Math.Min(1.0, (double)MaxWidth / width);
        return new Size(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    public static Bitmap Decode(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            using var source = Image.FromStream(stream);
            return new Bitmap(source, Fit(source.Width, source.Height));
        }
        catch
        {
            using var image = SharpImage.Load(data); // first frame only
            var size = Fit(image.Width, image.Height);
            image.Mutate(context => context.Resize(size.Width, size.Height));
            using var png = new MemoryStream();
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, png);
            png.Position = 0;
            using var decoded = Image.FromStream(png);
            return new Bitmap(decoded); // a copy, so it outlives the stream
        }
    }
}
