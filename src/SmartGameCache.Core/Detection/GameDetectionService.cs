using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SmartGameCache.Core.Models;
using SmartGameCache.Core.Storage;

namespace SmartGameCache.Core.Detection;

public class GameDetectionService
{
    public List<GameInfo> DetectAllInstalledGames()
    {
        var games = new List<GameInfo>();

        try
        {
            games.AddRange(DetectSteamGames());
        }
        catch { }

        try
        {
            games.AddRange(DetectEpicGames());
        }
        catch { }

        try
        {
            games.AddRange(DetectRockstarGames());
        }
        catch { }

        return games;
    }

    public List<GameInfo> DetectSteamGames()
    {
        var list = new List<GameInfo>();

        string? steamPath = null;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
        {
            steamPath = key?.GetValue("SteamPath")?.ToString()?.Replace('/', '\\');
        }

        if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath))
        {
            using var key64 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            steamPath = key64?.GetValue("InstallPath")?.ToString();
        }

        if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath))
        {
            return list;
        }

        var libraryFolders = new List<string> { steamPath };

        // Parse libraryfolders.vdf
        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            try
            {
                var content = File.ReadAllText(vdfPath);
                var matches = Regex.Matches(content, @"""path""\s+""([^""]+)""");
                foreach (Match m in matches)
                {
                    var p = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(p) && !libraryFolders.Contains(p, StringComparer.OrdinalIgnoreCase))
                    {
                        libraryFolders.Add(p);
                    }
                }
            }
            catch { }
        }

        foreach (var lib in libraryFolders)
        {
            var appsDir = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(appsDir)) continue;

            var manifests = Directory.GetFiles(appsDir, "appmanifest_*.acf");
            foreach (var mf in manifests)
            {
                try
                {
                    var text = File.ReadAllText(mf);
                    var nameMatch = Regex.Match(text, @"""name""\s+""([^""]+)""");
                    var dirMatch = Regex.Match(text, @"""installdir""\s+""([^""]+)""");

                    if (nameMatch.Success && dirMatch.Success)
                    {
                        var name = nameMatch.Groups[1].Value;
                        // Skip Steamworks common redistributables
                        if (name.Contains("Steamworks", StringComparison.OrdinalIgnoreCase)) continue;

                        var installFolder = Path.Combine(appsDir, "common", dirMatch.Groups[1].Value);
                        if (!Directory.Exists(installFolder)) continue;

                        var exe = FindMainExecutable(installFolder);
                        if (string.IsNullOrEmpty(exe)) continue;

                        var root = Path.GetPathRoot(installFolder) ?? "C:\\";
                        var mediaType = DriveDetector.GetMediaTypeForPath(installFolder);

                        list.Add(new GameInfo
                        {
                            Name = name,
                            ExecutablePath = exe,
                            InstallDirectory = installFolder,
                            DriveLetter = root.TrimEnd('\\'),
                            StorageType = mediaType,
                            LauncherSource = "Steam",
                            TotalInstallSizeBytes = CalculateDirectorySize(installFolder),
                            AddedAt = DateTime.UtcNow
                        });
                    }
                }
                catch { }
            }
        }

        return list;
    }

    public List<GameInfo> DetectEpicGames()
    {
        var list = new List<GameInfo>();
        var manifestsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (!Directory.Exists(manifestsDir)) return list;

        var itemFiles = Directory.GetFiles(manifestsDir, "*.item");
        foreach (var itemFile in itemFiles)
        {
            try
            {
                var json = File.ReadAllText(itemFile);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("DisplayName", out var nameProp) &&
                    root.TryGetProperty("InstallLocation", out var locProp) &&
                    root.TryGetProperty("LaunchExecutable", out var exeProp))
                {
                    var name = nameProp.GetString();
                    var loc = locProp.GetString();
                    var exeRel = exeProp.GetString();

                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(loc) && Directory.Exists(loc))
                    {
                        var fullExe = !string.IsNullOrEmpty(exeRel) ? Path.Combine(loc, exeRel) : FindMainExecutable(loc);
                        if (string.IsNullOrEmpty(fullExe) || !File.Exists(fullExe))
                        {
                            fullExe = FindMainExecutable(loc);
                        }

                        if (!string.IsNullOrEmpty(fullExe) && File.Exists(fullExe))
                        {
                            var driveRoot = Path.GetPathRoot(loc) ?? "C:\\";
                            var mediaType = DriveDetector.GetMediaTypeForPath(loc);
                            long size = 0;
                            if (root.TryGetProperty("InstallSize", out var sizeProp))
                            {
                                size = sizeProp.GetInt64();
                            }

                            if (size == 0) size = CalculateDirectorySize(loc);

                            list.Add(new GameInfo
                            {
                                Name = name,
                                ExecutablePath = fullExe,
                                InstallDirectory = loc,
                                DriveLetter = driveRoot.TrimEnd('\\'),
                                StorageType = mediaType,
                                LauncherSource = "Epic Games",
                                TotalInstallSizeBytes = size,
                                AddedAt = DateTime.UtcNow
                            });
                        }
                    }
                }
            }
            catch { }
        }

        return list;
    }

    public List<GameInfo> DetectRockstarGames()
    {
        var list = new List<GameInfo>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V");
            var path = key?.GetValue("InstallFolder")?.ToString();
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                var exe = Path.Combine(path, "GTA5.exe");
                if (File.Exists(exe))
                {
                    var driveRoot = Path.GetPathRoot(path) ?? "C:\\";
                    list.Add(new GameInfo
                    {
                        Name = "Grand Theft Auto V",
                        ExecutablePath = exe,
                        InstallDirectory = path,
                        DriveLetter = driveRoot.TrimEnd('\\'),
                        StorageType = DriveDetector.GetMediaTypeForPath(path),
                        LauncherSource = "Rockstar Games",
                        TotalInstallSizeBytes = CalculateDirectorySize(path),
                        AddedAt = DateTime.UtcNow
                    });
                }
            }
        }
        catch { }

        return list;
    }

    public GameInfo? CreateManualGame(string executablePath, string? customName = null)
    {
        if (!File.Exists(executablePath)) return null;

        var installDir = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(installDir)) return null;

        var name = !string.IsNullOrWhiteSpace(customName)
            ? customName
            : Path.GetFileNameWithoutExtension(executablePath);

        var driveRoot = Path.GetPathRoot(installDir) ?? "C:\\";
        var mediaType = DriveDetector.GetMediaTypeForPath(installDir);

        return new GameInfo
        {
            Name = name,
            ExecutablePath = Path.GetFullPath(executablePath),
            InstallDirectory = Path.GetFullPath(installDir),
            DriveLetter = driveRoot.TrimEnd('\\'),
            StorageType = mediaType,
            LauncherSource = "Manual",
            TotalInstallSizeBytes = CalculateDirectorySize(installDir),
            AddedAt = DateTime.UtcNow
        };
    }

    private static string? FindMainExecutable(string directory)
    {
        try
        {
            var exes = Directory.GetFiles(directory, "*.exe", SearchOption.TopDirectoryOnly);
            // Ignore uninstaller, crash handler, helper tools
            var filtered = new List<string>();
            foreach (var e in exes)
            {
                var fn = Path.GetFileName(e).ToLowerInvariant();
                if (fn.Contains("uninstall") || fn.Contains("crash") || fn.Contains("setup") || fn.Contains("helper") || fn.Contains("update") || fn.Contains("redist"))
                {
                    continue;
                }
                filtered.Add(e);
            }

            if (filtered.Count > 0)
            {
                // Return largest executable by file size (typically game binary)
                return filtered.OrderByDescending(f => new FileInfo(f).Length).First();
            }

            // Check subfolders (e.g. Binaries/Win64)
            var subExes = Directory.GetFiles(directory, "*.exe", SearchOption.AllDirectories);
            foreach (var e in subExes)
            {
                var fn = Path.GetFileName(e).ToLowerInvariant();
                if (fn.Contains("shipping") || fn.Contains("win64") || fn.Contains("game"))
                {
                    return e;
                }
            }

            return subExes.Length > 0 ? subExes[0] : null;
        }
        catch
        {
            return null;
        }
    }

    private static long CalculateDirectorySize(string directory)
    {
        try
        {
            long size = 0;
            var dirInfo = new DirectoryInfo(directory);
            foreach (var f in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                size += f.Length;
            }
            return size;
        }
        catch
        {
            return 0;
        }
    }
}

