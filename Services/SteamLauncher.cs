using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Steamalt;

internal static partial class Program
{
    private static void FocusProcess(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo("wmctrl")
                {
                    ArgumentList = { "-ia", process.Id.ToString() },
                    UseShellExecute = false
                });
            }
            _status = $"Requested focus for process {processId}. Install wmctrl if the window did not focus.";
            _isError = false;
        }
        catch (Exception ex)
        {
            _status = $"Could not focus process {processId}: {ex.Message}";
            _isError = true;
        }
    }

    private static void StopProcess(int processId, bool force)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                string signal = force ? "-KILL" : "-TERM";
                Process.Start(new ProcessStartInfo("kill")
                {
                    ArgumentList = { signal, processId.ToString() },
                    UseShellExecute = false
                });
            }
            else
            {
                using Process process = Process.GetProcessById(processId);
                if (force)
                    process.Kill(entireProcessTree: true);
                else
                    process.CloseMainWindow();
            }
            _status = force ? $"Force-stopped process {processId}." : $"Requested shutdown for process {processId}.";
            _isError = false;
        }
        catch (Exception ex)
        {
            _status = $"Could not stop process {processId}: {ex.Message}";
            _isError = true;
        }
    }

    private static void LaunchProfile(SteamProfile profile)
    {
        try
        {
            string profileHome = Path.Combine(ProfilesDirectory, profile.Id.ToString("N"));
            Directory.CreateDirectory(profileHome);
            string hostSteamRoot = FindSteamRoot();
            string steamRoot = EnsureProfileSteamClient(profileHome, hostSteamRoot);
            EnsureProfileSteamLinks(profileHome, steamRoot);
            IReadOnlyList<string> steamLibraries = FindSteamLibraries(steamRoot);
            IReadOnlyList<string> compatibilityToolPaths = FindCompatibilityToolPaths(
                hostSteamRoot,
                steamLibraries);

            var startInfo = CreateFirejailStartInfo(
                profileHome,
                steamRoot,
                steamLibraries,
                compatibilityToolPaths,
                EnsureDolphinFileManager(profileHome));
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(SteamExecutable);

            Process? process = Process.Start(startInfo);
            if (process is null)
                throw new InvalidOperationException("The operating system did not start Firejail.");

            _launchedFirejailProcesses[process.Id] = profile;
            _runningProcesses.Add(new SteamProcessInfo
            {
                ProcessId = process.Id,
                ProfileId = profile.Id,
                ProfileName = profile.Name
            });
            _status = $"Started Steam for {profile.Name} (PID {process.Id}).";
            _isError = false;
        }
        catch (Exception ex)
        {
            _status = $"Could not start Steam for {profile.Name}: {ex.Message}";
            _isError = true;
        }
    }

    private static ProcessStartInfo CreateFirejailStartInfo(
        string profileHome,
        string steamRoot,
        IReadOnlyList<string> steamLibraries,
        IReadOnlyList<string> compatibilityToolPaths,
        string? fileManager)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FirejailExecutable,
            UseShellExecute = false
        };
        startInfo.Environment.Remove("STEAM_COMPAT_DATA_PATH");
        startInfo.ArgumentList.Add("--ignore=seccomp");
        startInfo.ArgumentList.Add("--ignore=whitelist");
        startInfo.ArgumentList.Add("--ignore=blacklist");
        startInfo.ArgumentList.Add("--profile=steam");
        startInfo.ArgumentList.Add("--allow-bwrap");
        startInfo.ArgumentList.Add($"--env=HOME={profileHome}");
        startInfo.ArgumentList.Add($"--env=XDG_CONFIG_HOME={Path.Combine(profileHome, ".config")}");
        startInfo.ArgumentList.Add($"--env=XDG_DATA_HOME={Path.Combine(profileHome, ".local", "share")}");
        startInfo.ArgumentList.Add($"--env=XDG_CACHE_HOME={Path.Combine(profileHome, ".cache")}");
        startInfo.ArgumentList.Add($"--env=XDG_STATE_HOME={Path.Combine(profileHome, ".local", "state")}");
        if (fileManager is not null)
        {
            // Open directories with the host's KDE file manager; without this
            // hint xdg-open has no usable default inside the profile jail.
            startInfo.ArgumentList.Add($"--env=FILEMANAGER={fileManager}");
        }
        startInfo.ArgumentList.Add($"--read-only={HostHomeDirectory}");
        startInfo.ArgumentList.Add($"--read-write={profileHome}");
        startInfo.ArgumentList.Add($"--read-write={steamRoot}");
        foreach (string library in steamLibraries)
            startInfo.ArgumentList.Add($"--read-write={library}");
        foreach (string compatibilityToolPath in compatibilityToolPaths)
            startInfo.ArgumentList.Add($"--read-only={compatibilityToolPath}");
        if (compatibilityToolPaths.Count > 0)
            startInfo.ArgumentList.Add($"--env=STEAM_COMPAT_TOOL_PATHS={string.Join(":", compatibilityToolPaths)}");
        return startInfo;
    }

    private static string FindSteamRoot()
    {
        string[] candidates =
        [
            Path.Combine(HostHomeDirectory, ".steam", "steam"),
            Path.Combine(DataDirectory, "Steam")
        ];
        string? steamRoot = candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "steamapps", "libraryfolders.vdf")));
        if (steamRoot is null)
            throw new DirectoryNotFoundException("Could not find the host Steam installation.");

        return new DirectoryInfo(steamRoot).ResolveLinkTarget(true)?.FullName ?? steamRoot;
    }

    // Point each profile's directory-opening at the host's KDE Dolphin. The
    // profile jail has its own HOME/XDG_CONFIG_HOME and a read-only host home,
    // so it has no directory -> file-manager association of its own; without
    // this, xdg-open on a folder inside the jail has nothing to fall back to.
    // Returns the Dolphin binary to pass via --env=FILEMANAGER, or null when
    // Dolphin isn't installed (leaving the profile's behaviour unchanged).
    private static string? EnsureDolphinFileManager(string profileHome)
    {
        string? dolphinPath = FindDolphin();
        if (dolphinPath is null)
            return null;

        string configFile = Path.Combine(profileHome, ".config", "mimeapps.list");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            if (!File.Exists(configFile))
            {
                File.WriteAllText(configFile,
                    "# Managed by Steamalt: open folders with the host's KDE Dolphin.\n" +
                    "[Default Applications]\n" +
                    "inode/directory=org.kde.dolphin.desktop\n" +
                    "x-directories=org.kde.dolphin.desktop\n");
            }
        }
        catch (Exception ex)
        {
            _status = $"Could not record the profile file-manager default: {ex.Message}";
            _isError = true;
        }

        return dolphinPath;
    }

    private static string? FindDolphin()
    {
        string[] candidates =
        {
            "/usr/bin/dolphin",
            "/usr/local/bin/dolphin",
            Path.Combine(HostHomeDirectory, ".local", "bin", "dolphin"),
        };
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IReadOnlyList<string> FindCompatibilityToolPaths(
        string hostSteamRoot,
        IReadOnlyList<string> steamLibraries)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<string>
        {
            Path.Combine(hostSteamRoot, "compatibilitytools.d"),
            Path.Combine(HostHomeDirectory, ".steam", "root", "compatibilitytools.d"),
            Path.Combine(DataDirectory, "Steam", "compatibilitytools.d"),
            "/usr/share/steam/compatibilitytools.d",
            "/usr/local/share/steam/compatibilitytools.d"
        };
        foreach (string library in steamLibraries)
            candidates.Add(Path.Combine(library, "compatibilitytools.d"));

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate))
                paths.Add(Path.GetFullPath(candidate));
        }

        string? environmentPaths = Environment.GetEnvironmentVariable("STEAM_COMPAT_TOOL_PATHS");
        if (!string.IsNullOrWhiteSpace(environmentPaths))
        {
            foreach (string path in environmentPaths.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Directory.Exists(path))
                    paths.Add(Path.GetFullPath(path));
            }
        }

        return paths.ToArray();
    }

    private static IReadOnlyList<string> FindSteamLibraries(string steamRoot)
    {
        string libraryFoldersPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        string libraryFolders = File.ReadAllText(libraryFoldersPath);
        var libraries = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.GetFullPath(steamRoot)
        };

        foreach (Match match in Regex.Matches(libraryFolders, @"""path""\s+""([^""]+)"""))
        {
            string library = Path.GetFullPath(match.Groups[1].Value.Replace("\\\\", "\\"));
            if (Directory.Exists(library))
                libraries.Add(new DirectoryInfo(library).ResolveLinkTarget(true)?.FullName ?? library);
        }

        return libraries.ToArray();
    }

    // Top-level, per-login state that never belongs in a client mirror.
    // Checked only at the profile Steam root so same-named client entries
    // deeper in the tree (e.g. "steamrt64/steam") are still synced.
    private static readonly HashSet<string> ProfileOwnedDirectories = new(StringComparer.Ordinal)
    {
        "steamapps", "userdata", "config", "logs", "appcache", "depotcache",
        "steam", "var", "compatibilitytools.d"
    };

    private static string EnsureProfileSteamClient(string profileHome, string hostSteamRoot)
    {
        string profileSteamRoot = Path.Combine(profileHome, ".local", "share", "Steam");
        if (new DirectoryInfo(profileSteamRoot).LinkTarget is not null)
        {
            new DirectoryInfo(profileSteamRoot).Delete();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(profileSteamRoot)!);
        SyncSteamClient(hostSteamRoot, profileSteamRoot, hostSteamRoot, profileSteamRoot, isRoot: true);

        string sourceLibraryFolders = Path.Combine(hostSteamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(sourceLibraryFolders))
        {
            string destinationSteamApps = Path.Combine(profileSteamRoot, "steamapps");
            Directory.CreateDirectory(destinationSteamApps);
            // Profiles share the host's libraries, so the library list always
            // tracks the host (overwrite: keeps resync idempotent).
            File.Copy(
                sourceLibraryFolders,
                Path.Combine(destinationSteamApps, "libraryfolders.vdf"),
                overwrite: true);
        }

        return profileSteamRoot;
    }

    // One-way, incremental mirror of the host Steam client into the profile
    // so profiles keep tracking host upgrades (e.g. the Scout -> SteamRT3
    // migration) instead of staying frozen at the version that was first
    // copied over. Per-login state (ProfileOwnedDirectories, root level
    // only) is never touched. Nothing on the destination side is deleted
    // except symlinks that this mirror replaces.
    private static void SyncSteamClient(
        string sourceDirectory,
        string destinationDirectory,
        string hostSteamRoot,
        string profileSteamRoot,
        bool isRoot)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string sourcePath in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            string name = Path.GetFileName(sourcePath);
            if (isRoot && ProfileOwnedDirectories.Contains(name))
                continue;

            string destinationPath = Path.Combine(destinationDirectory, name);

            // Mirror links as links (matching the host layout and the
            // known-good profile trees) instead of duplicating their
            // targets; covers links to files, directories, and dangling.
            if (GetLinkTarget(sourcePath) is not null)
            {
                SyncSymlink(sourcePath, destinationPath, hostSteamRoot, profileSteamRoot);
                continue;
            }

            if (Directory.Exists(sourcePath))
            {
                SyncSteamClient(
                    sourcePath, destinationPath, hostSteamRoot, profileSteamRoot, isRoot: false);
                continue;
            }

            if (File.Exists(sourcePath))
                SyncRegularFile(sourcePath, destinationPath);
        }
    }

    private static void SyncRegularFile(string sourcePath, string destinationPath)
    {
        var sourceInfo = new FileInfo(sourcePath);
        var destinationInfo = new FileInfo(destinationPath);

        if (destinationInfo.LinkTarget is not null)
        {
            // A profile symlink (old Scout shim) that the host now keeps as
            // a regular file: unlink it and materialize the real file. A
            // link already resolved to this source is left as-is.
            if (destinationInfo.ResolveLinkTarget(true)?.FullName == sourcePath)
                return;
            File.Delete(destinationPath);
        }
        else if (destinationInfo.Exists
            && destinationInfo.Length == sourceInfo.Length
            && destinationInfo.LastWriteTimeUtc == sourceInfo.LastWriteTimeUtc)
        {
            return;
        }

        // Atomic replace so a running process never sees a half-written
        // binary; SetUnixFileMode keeps the executable bit.
        string tempPath = destinationPath + ".sync.tmp";
        File.Copy(sourcePath, tempPath, overwrite: true);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(tempPath, File.GetUnixFileMode(sourcePath));
        File.Move(tempPath, destinationPath, overwrite: true);
    }

    private static string? GetLinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void SyncSymlink(
        string sourcePath,
        string destinationPath,
        string hostSteamRoot,
        string profileSteamRoot)
    {
        string target = new FileInfo(sourcePath).LinkTarget
            ?? new DirectoryInfo(sourcePath).LinkTarget
            ?? throw new InvalidOperationException($"Missing link target for {sourcePath}");
        string desiredTarget = RewriteHostRootLinkTarget(target, hostSteamRoot, profileSteamRoot);

        if (GetLinkTarget(destinationPath) is not null)
        {
            // File.Delete unlinks stale and dangling symlinks alike;
            // ResolveLinkTarget compares the actual target so a same-path
            // link is not needlessly replaced.
            if (new FileInfo(destinationPath).ResolveLinkTarget(true)?.FullName
                    == Path.GetFullPath(sourcePath)
                || new FileInfo(destinationPath).LinkTarget!.Equals(
                    desiredTarget, StringComparison.Ordinal))
                return;
            File.Delete(destinationPath);
        }
        else if (Directory.Exists(destinationPath))
        {
            // A profile-owned directory (e.g. pressure-vessel scratch) at
            // the same path as a host link; keep it and skip the link.
            return;
        }
        else if (File.Exists(destinationPath))
        {
            new FileInfo(destinationPath).Delete();
        }

        File.CreateSymbolicLink(destinationPath, desiredTarget);
    }

    // Absolute links that sit inside the host Steam root are rewritten
    // onto the profile's own mirror so each profile stays self-contained
    // (mirrors the layout of known-good profiles, where runtime tool links
    // point into the profile tree, not the host). Relative links and links
    // to system paths (e.g. /usr/lib/millennium/...) are kept verbatim.
    private static string RewriteHostRootLinkTarget(
        string target,
        string hostSteamRoot,
        string profileSteamRoot)
    {
        if (!Path.IsPathRooted(target))
            return target;

        string fullTarget = Path.GetFullPath(target);
        string hostRoot = Path.GetFullPath(hostSteamRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!fullTarget.StartsWith(hostRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return target;

        string offset = fullTarget[(hostRoot.Length + 1)..];
        return Path.GetFullPath(Path.Combine(profileSteamRoot, offset));
    }

    private static void EnsureProfileSteamLinks(string profileHome, string steamRoot)
    {
        string steamConfigDirectory = Path.Combine(profileHome, ".steam");
        Directory.CreateDirectory(steamConfigDirectory);
        foreach (string linkName in new[] { "steam", "root" })
        {
            string linkPath = Path.Combine(steamConfigDirectory, linkName);
            var linkInfo = new DirectoryInfo(linkPath);
            if (linkInfo.LinkTarget is not null)
            {
                string currentTarget = linkInfo.ResolveLinkTarget(true)?.FullName
                    ?? Path.GetFullPath(
                        Path.IsPathRooted(linkInfo.LinkTarget)
                            ? linkInfo.LinkTarget
                            : Path.Combine(steamConfigDirectory, linkInfo.LinkTarget));
                if (currentTarget == steamRoot)
                    continue;
                linkInfo.Delete();
            }
            else if (Directory.Exists(linkPath) || File.Exists(linkPath))
            {
                throw new IOException($"Steam profile path is not a symbolic link: {linkPath}");
            }

            Directory.CreateSymbolicLink(linkPath, steamRoot);
        }
    }

}
