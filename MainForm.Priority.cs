using System.Text.RegularExpressions;

namespace R2MM;

// Conflict detection, load order (renaming PAKs so they sort in the chosen order),
// drag-and-drop reordering and Confirm / Revert.
internal sealed partial class MainForm
{
    // ---- Conflict detection -------------------------------------------------------------------

    private string? FindExistingPakPath(ModEntry mod)
    {
        foreach (var path in new[]
        {
            Path.Combine(_paksDirectory, mod.FileName),
            GetDisabledPath(mod.FileName)
        })
        {
            if (File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }

    private void LoadFileSets()
    {
        _fileSets.Clear();
        foreach (var mod in _mods)
        {
            _fileSets[mod.FileName] = LoadFileSet(mod);
        }
    }

    // Reads the PAK's own index first; falls back to "<name>.pak.list" beside it when the index
    // cannot be read (encrypted or unsupported PAK version).
    private ModFileSet LoadFileSet(ModEntry mod)
    {
        var pakPath = FindExistingPakPath(mod);
        if (pakPath is null)
        {
            return new ModFileSet(null, "", "The PAK file is missing.");
        }

        try
        {
            var sidecarPath = Path.Combine(_paksDirectory, mod.FileName + ".list");
            var pakInfo = new FileInfo(pakPath);
            var sidecarInfo = new FileInfo(sidecarPath);
            var stamp = $"{pakInfo.Length}|{pakInfo.LastWriteTimeUtc.Ticks}|{(sidecarInfo.Exists ? sidecarInfo.LastWriteTimeUtc.Ticks : 0)}";
            if (_fileSetCache.TryGetValue(pakPath, out var cached) && cached.Stamp == stamp)
            {
                return cached.Set;
            }

            ModFileSet set;
            try
            {
                set = new ModFileSet(PakIndexReader.ReadFileList(pakPath), "PAK index", null);
            }
            catch (Exception ex) when (ex is PakFormatException or IOException or UnauthorizedAccessException)
            {
                set = sidecarInfo.Exists
                    ? new ModFileSet(PakIndexReader.ReadSidecarList(sidecarPath), "list file", null)
                    : new ModFileSet(null, "", $"{ex.Message} Add a '{mod.FileName}.list' file beside the PAK to enable conflict detection.");
            }
            _fileSetCache[pakPath] = (stamp, set);
            return set;
        }
        catch (Exception ex)
        {
            return new ModFileSet(null, "", ex.Message);
        }
    }

    private static string JoinNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 3
            ? string.Join(", ", list)
            : $"{string.Join(", ", list.Take(3))} +{list.Count - 3} more";
    }

    // Conflicts are evaluated for the mods that are checked (so you see the effect before pressing
    // Confirm Changes). Mods are in load order; when several contain the same file, the last one wins.
    private void UpdateConflictColumn()
    {
        var enabled = new List<(ListViewItem Item, ModFileSet Set)>();
        foreach (ListViewItem item in _modList.Items)
        {
            if (item.Checked && item.Tag is string fileName && _fileSets.TryGetValue(fileName, out var set))
            {
                enabled.Add((item, set));
            }
        }

        var owners = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < enabled.Count; i++)
        {
            if (enabled[i].Set.Files is not { } files)
            {
                continue;
            }
            foreach (var file in files)
            {
                if (!owners.TryGetValue(file, out var list))
                {
                    owners[file] = list = [];
                }
                list.Add(i);
            }
        }

        var wins = new int[enabled.Count];
        var losses = new int[enabled.Count];
        var beats = new HashSet<string>[enabled.Count];
        var beatenBy = new HashSet<string>[enabled.Count];
        var samples = new List<string>[enabled.Count];
        for (var i = 0; i < enabled.Count; i++)
        {
            beats[i] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            beatenBy[i] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            samples[i] = [];
        }

        var rows = new List<ConflictRow>();
        foreach (var (path, list) in owners)
        {
            if (list.Count < 2)
            {
                continue;
            }

            var winner = list[^1];
            var winnerName = enabled[winner].Item.Text;
            wins[winner]++;
            if (samples[winner].Count < 10)
            {
                samples[winner].Add(path);
            }
            for (var k = 0; k < list.Count - 1; k++)
            {
                var loser = list[k];
                losses[loser]++;
                beats[winner].Add(enabled[loser].Item.Text);
                beatenBy[loser].Add(winnerName);
                if (samples[loser].Count < 10)
                {
                    samples[loser].Add(path);
                }
            }
            rows.Add(new ConflictRow(
                path,
                winnerName,
                string.Join(", ", list.Take(list.Count - 1).Select(index => enabled[index].Item.Text))));
        }

        var indexByItem = new Dictionary<ListViewItem, int>();
        for (var i = 0; i < enabled.Count; i++)
        {
            indexByItem[enabled[i].Item] = i;
        }

        var amber = Theme.Highlight;
        foreach (ListViewItem item in _modList.Items)
        {
            if (item.SubItems.Count < 4)
            {
                continue;
            }

            var text = "";
            var tip = "";
            var color = TextColor;
            if (item.Checked && indexByItem.TryGetValue(item, out var i))
            {
                if (enabled[i].Set.Files is null)
                {
                    text = "Unknown (no readable file list)";
                    tip = enabled[i].Set.Error ?? "";
                    color = MutedColor;
                }
                else if (wins[i] == 0 && losses[i] == 0)
                {
                    text = "None";
                    tip = $"File list read from the {enabled[i].Set.Source}.";
                }
                else
                {
                    var parts = new List<string>();
                    if (wins[i] > 0)
                    {
                        parts.Add($"overrides {wins[i]} file(s) of {JoinNames(beats[i])}");
                    }
                    if (losses[i] > 0)
                    {
                        parts.Add($"overridden on {losses[i]} file(s) by {JoinNames(beatenBy[i])}");
                    }
                    text = string.Join("; ", parts);
                    var tipLines = new List<string>(samples[i]);
                    if (wins[i] + losses[i] > samples[i].Count)
                    {
                        tipLines.Add($"...and {wins[i] + losses[i] - samples[i].Count} more");
                    }
                    tip = string.Join("\n", tipLines);
                    color = amber;
                }
            }

            if (item.SubItems[3].Text != text)
            {
                item.SubItems[3].Text = text;
            }
            item.ToolTipText = tip;
            if (item.ForeColor != color)
            {
                item.ForeColor = color;
            }
        }

        _conflictRows = rows.OrderBy(row => row.Asset, StringComparer.Ordinal).ToList();
        _unreadableMods = enabled.Where(entry => entry.Set.Files is null).Select(entry => entry.Item.Text).ToList();
        _conflictsButton.Text = _conflictRows.Count > 0 ? $"View Conflicts ({_conflictRows.Count})" : "View Conflicts";
    }

    private void ShowConflicts()
    {
        using var dialog = new ConflictsDialog(_conflictRows, _unreadableMods);
        dialog.ShowDialog(this);
    }

    // ---- Load order ---------------------------------------------------------------------------

    // Unreal mounts PAKs in file-name order, so the load order is whatever the names sort to. The
    // manager keeps its own order (_mods) and renames files with a numeric prefix to match it.
    private bool HasPendingOrder()
    {
        if (_mods.Count < 2)
        {
            return false;
        }
        var sorted = _mods
            .OrderBy(mod => mod.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(mod => mod.FileName);
        return !sorted.SequenceEqual(_mods.Select(mod => mod.FileName), StringComparer.OrdinalIgnoreCase);
    }

    private static string StripOrderPrefix(string fileName)
    {
        var match = Regex.Match(fileName, @"^\d{3,4}_");
        if (!match.Success)
        {
            return fileName;
        }
        var stripped = fileName[match.Length..];
        return IsSafePakFileName(stripped) && Path.GetFileNameWithoutExtension(stripped).Length > 0
            ? stripped
            : fileName;
    }

    private List<(ModEntry Mod, string NewName)> BuildRenamePlan()
    {
        var plan = new List<(ModEntry Mod, string NewName)>();
        var width = Math.Max(3, _mods.Count.ToString().Length);
        for (var i = 0; i < _mods.Count; i++)
        {
            var newName = $"{(i + 1).ToString().PadLeft(width, '0')}_{StripOrderPrefix(_mods[i].FileName)}";
            if (!newName.Equals(_mods[i].FileName, StringComparison.Ordinal))
            {
                plan.Add((_mods[i], newName));
            }
        }
        return plan;
    }

    // Renames the PAKs so their names sort in the displayed order.
    // Returns the number of mods renamed, or -1 if it failed (an error was already shown).
    private int ApplyLoadOrder()
    {
        if (!HasPendingOrder())
        {
            return 0;
        }

        var plan = BuildRenamePlan();
        if (plan.Count == 0)
        {
            return 0;
        }

        var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (ListViewItem item in _modList.Items)
        {
            if (item.Tag is string name)
            {
                states[name] = item.Checked;
            }
        }
        var renamed = plan.Select(entry => (Old: entry.Mod.FileName, entry.NewName)).ToList();

        try
        {
            RenameMods(plan);
            var newStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var (oldName, newName) in renamed)
            {
                if (states.TryGetValue(oldName, out var checkedState))
                {
                    newStates[newName] = checkedState;
                }
            }
            RefreshModList(preserveDesiredStates: true, stateOverrides: newStates);
            return plan.Count;
        }
        catch (Exception ex)
        {
            RefreshModList(preserveDesiredStates: true);
            ShowError("Could not apply the load order. No files were changed.", ex);
            return -1;
        }
    }

    // ---- Confirm / revert ---------------------------------------------------------------------

    private void ConfirmChanges()
    {
        if (!EnsureGameSelected())
        {
            return;
        }

        var toggled = ApplyModChanges();
        if (toggled < 0)
        {
            return;
        }
        var renamed = ApplyLoadOrder();
        if (renamed < 0)
        {
            return;
        }

        SetStatus(toggled == 0 && renamed == 0
            ? "No changes to confirm."
            : $"{toggled} mod(s) enabled/disabled, changed loading priorty for {renamed} mod(s).");
    }

    // Back to what is on disk: the confirmed enabled/disabled state and the confirmed load order.
    private void RevertChanges()
    {
        SortModsByFileName();
        RefreshModList();
        SetStatus("Returned to last save state.");
    }

    private void SortModsByFileName() =>
        _mods.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));

    // ---- Drag and drop reordering -------------------------------------------------------------

    private const string ModDragFormat = "Redout2ModManager.ModOrder";

    private void BeginModDrag(object? sender, ItemDragEventArgs e)
    {
        if (e.Item is not ListViewItem dragged)
        {
            return;
        }
        if (!dragged.Selected)
        {
            _modList.SelectedItems.Clear();
            dragged.Selected = true;
        }

        var names = _modList.SelectedItems.Cast<ListViewItem>()
            .Select(item => item.Tag as string)
            .Where(name => name is not null)
            .Cast<string>()
            .ToArray();
        var data = new DataObject();
        data.SetData(ModDragFormat, names);
        _modList.DoDragDrop(data, DragDropEffects.Move);
        ClearDropIndicator();
    }

    private int GetInsertionIndex(DragEventArgs e)
    {
        var point = _modList.PointToClient(new Point(e.X, e.Y));
        var item = _modList.GetItemAt(8, point.Y);
        if (item is null)
        {
            return _modList.Items.Count == 0 || point.Y < _modList.Items[0].Bounds.Top ? 0 : _modList.Items.Count;
        }
        return point.Y > item.Bounds.Top + item.Bounds.Height / 2 ? item.Index + 1 : item.Index;
    }

    private void HandleModDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data is null || !e.Data.GetDataPresent(ModDragFormat))
        {
            return;
        }
        e.Effect = DragDropEffects.Move;

        var point = _modList.PointToClient(new Point(e.X, e.Y));
        if (_modList.Items.Count > 0)
        {
            // Scroll when the pointer is near the top or bottom edge.
            if (point.Y < 40 && _modList.TopItem is { } top && top.Index > 0)
            {
                _modList.EnsureVisible(top.Index - 1);
            }
            else if (point.Y > _modList.ClientSize.Height - 16)
            {
                var last = _modList.GetItemAt(8, _modList.ClientSize.Height - 4);
                var next = Math.Min(_modList.Items.Count - 1, (last?.Index ?? _modList.Items.Count - 1) + 1);
                _modList.EnsureVisible(next);
            }
        }

        var index = GetInsertionIndex(e);
        if (((BufferedListView)_modList).InsertionIndex != index)
        {
            ((BufferedListView)_modList).InsertionIndex = index;
        }
    }

    private void ClearDropIndicator()
    {
        if (_modList is BufferedListView list && list.InsertionIndex != -1)
        {
            list.InsertionIndex = -1;
        }
    }

    private void HandleModDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(ModDragFormat) is not string[] names)
        {
            return;
        }
        var index = GetInsertionIndex(e);
        ClearDropIndicator();
        ReorderMods(names, index);
    }

    private void ReorderMods(IReadOnlyCollection<string> names, int insertionIndex)
    {
        var selected = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var moving = _mods.Where(mod => selected.Contains(mod.FileName)).ToList();
        if (moving.Count == 0)
        {
            return;
        }

        var before = _mods.Take(insertionIndex).Count(mod => selected.Contains(mod.FileName));
        var original = _mods.Select(mod => mod.FileName).ToList();
        _mods.RemoveAll(mod => selected.Contains(mod.FileName));
        _mods.InsertRange(Math.Clamp(insertionIndex - before, 0, _mods.Count), moving);
        if (original.SequenceEqual(_mods.Select(mod => mod.FileName)))
        {
            return;
        }

        RefreshModList(preserveDesiredStates: true);
        foreach (ListViewItem item in _modList.Items)
        {
            if (item.Tag is string name && selected.Contains(name))
            {
                item.Selected = true;
            }
        }
        SetStatus("Mod position changed");
    }

    private IEnumerable<(string Source, string Destination)> GetModFileMoves(string oldName, string newName)
    {
        var oldSignature = Path.ChangeExtension(oldName, ".sig");
        var newSignature = Path.ChangeExtension(newName, ".sig");
        var candidates = new (string Source, string Destination)[]
        {
            (Path.Combine(_paksDirectory, oldName), Path.Combine(_paksDirectory, newName)),
            (GetDisabledPath(oldName), GetDisabledPath(newName)),
            (Path.Combine(_paksDirectory, oldSignature), Path.Combine(_paksDirectory, newSignature)),
            (GetDisabledPath(oldSignature), GetDisabledPath(newSignature)),
            (Path.Combine(_paksDirectory, oldName + ".list"), Path.Combine(_paksDirectory, newName + ".list"))
        };
        return candidates.Where(pair => File.Exists(pair.Source));
    }

    // Renames in two phases (old -> temporary -> new) so that mods can swap names, and rolls
    // everything back if any step fails.
    private void RenameMods(List<(ModEntry Mod, string NewName)> plan)
    {
        var moves = new List<(string Source, string Destination)>();
        foreach (var (mod, newName) in plan)
        {
            if (!IsSafePakFileName(newName))
            {
                throw new InvalidDataException($"Invalid PAK filename: {newName}");
            }
            moves.AddRange(GetModFileMoves(mod.FileName, newName));
        }

        var sources = moves.Select(move => move.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var move in moves)
        {
            if (!destinations.Add(move.Destination))
            {
                throw new IOException($"Two files would be renamed to {Path.GetFileName(move.Destination)}.");
            }
            if (File.Exists(move.Destination) && !sources.Contains(move.Destination))
            {
                throw new IOException($"Refusing to overwrite an existing file: {Path.GetFileName(move.Destination)}");
            }
            if (File.Exists(move.Source + ".reorder.tmp"))
            {
                throw new IOException($"A leftover temporary file exists: {Path.GetFileName(move.Source)}.reorder.tmp");
            }
        }

        var previousNames = plan.Select(entry => (entry.Mod, entry.Mod.FileName, entry.Mod.DisplayName)).ToList();
        var staged = new List<(string Source, string Temp, string Destination)>();
        var finalized = new List<(string Source, string Temp, string Destination)>();
        try
        {
            foreach (var move in moves)
            {
                var temp = move.Source + ".reorder.tmp";
                File.Move(move.Source, temp);
                staged.Add((move.Source, temp, move.Destination));
            }
            foreach (var stage in staged)
            {
                File.Move(stage.Temp, stage.Destination);
                finalized.Add(stage);
            }

            foreach (var (mod, newName) in plan)
            {
                mod.FileName = newName;
                mod.DisplayName = Path.GetFileNameWithoutExtension(newName);
            }
            SaveManifest();
        }
        catch (Exception operationError)
        {
            foreach (var (mod, fileName, displayName) in previousNames)
            {
                mod.FileName = fileName;
                mod.DisplayName = displayName;
            }

            var rollbackErrors = new List<Exception>();
            foreach (var stage in finalized.AsEnumerable().Reverse())
            {
                try
                {
                    File.Move(stage.Destination, stage.Temp);
                }
                catch (Exception ex)
                {
                    rollbackErrors.Add(ex);
                }
            }
            foreach (var stage in staged.AsEnumerable().Reverse())
            {
                try
                {
                    File.Move(stage.Temp, stage.Source);
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

    private sealed record ModFileSet(HashSet<string>? Files, string Source, string? Error);
}
