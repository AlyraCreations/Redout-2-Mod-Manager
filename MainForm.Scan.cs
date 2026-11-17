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

    private static string? FindPaksDirectory(string gameDirectory)
    {
        if (!Directory.Exists(gameDirectory))
        {
            return null;
        }

        var selectedDirectory = new DirectoryInfo(Path.GetFullPath(gameDirectory));
        if (selectedDirectory.Name.Equals("Paks", StringComparison.OrdinalIgnoreCase) &&
            selectedDirectory.Parent?.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) == true)
        {
            return selectedDirectory.FullName;
        }
        if (selectedDirectory.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(Path.Combine(selectedDirectory.FullName, "Paks")))
        {
            return Path.Combine(selectedDirectory.FullName, "Paks");
        }

        var candidates = new[]
        {
            Path.Combine(gameDirectory, "redout2", "Content", "Paks"),
            Path.Combine(gameDirectory, "Redout2", "Content", "Paks"),
            Path.Combine(gameDirectory, "Content", "Paks")
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }


    private int DiscoverInstalledMods()
    {
        if (string.IsNullOrWhiteSpace(_paksDirectory) || !Directory.Exists(_paksDirectory))
        {
            return 0;
        }

        var discovered = new List<ModEntry>();
        foreach (var path in Directory.EnumerateFiles(_paksDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (!IsPakImportPath(fileName))
            {
                continue;
            }

            var pakFileName = GetPakFileName(fileName);
            if (!IsSafePakFileName(pakFileName) || IsBaseGamePakFile(pakFileName))
            {
                continue;
            }

            if (_mods.Any(mod => mod.FileName.Equals(pakFileName, StringComparison.OrdinalIgnoreCase)) ||
                discovered.Any(mod => mod.FileName.Equals(pakFileName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var signatureName = Path.ChangeExtension(pakFileName, ".sig");
            var hasSignature = File.Exists(Path.Combine(_paksDirectory, signatureName)) ||
                               File.Exists(GetDisabledPath(signatureName));
            discovered.Add(new ModEntry
            {
                FileName = pakFileName,
                DisplayName = Path.GetFileNameWithoutExtension(pakFileName),
                HasSignature = hasSignature
            });
        }

        if (discovered.Count == 0)
        {
            return 0;
        }

        discovered.Sort((a, b) => string.Compare(
            a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));
        _mods.AddRange(discovered);
        SaveManifest();
        return discovered.Count;
    }


    private static bool IsBaseGamePakFile(string fileName)
    {
        return fileName.StartsWith("pakchunk", StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith("WindowsNoEditor.pak", StringComparison.OrdinalIgnoreCase);
    }

}
