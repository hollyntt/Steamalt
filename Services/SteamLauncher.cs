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
            string steamRoot = EnsureProfileSteamInstall(profileHome, hostSteamRoot);
            EnsureProfileSteamLinks(profileHome, steamRoot);
            IReadOnlyList<string> steamLibraries = FindSteamLibraries(steamRoot);
            IReadOnlyList<string> compatibilityToolPaths = FindCompatibilityToolPaths(
                hostSteamRoot,
                steamLibraries);

            var startInfo = CreateFirejailStartInfo(
                profileHome,
                steamRoot,
                steamLibraries,
                compatibilityToolPaths);
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
        IReadOnlyList<string> compatibilityToolPaths)
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

    private static string EnsureProfileSteamInstall(string profileHome, string hostSteamRoot)
    {
        string profileSteamRoot = Path.Combine(profileHome, ".local", "share", "Steam");
        var profileSteamInfo = new DirectoryInfo(profileSteamRoot);
        if (profileSteamInfo.LinkTarget is not null)
        {
            profileSteamInfo.Delete();
        }
        else if (Directory.Exists(Path.Combine(profileSteamRoot, "steamapps")))
        {
            return profileSteamRoot;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(profileSteamRoot)!);
        CopySteamClient(hostSteamRoot, profileSteamRoot);
        return profileSteamRoot;
    }

    private static void CopySteamClient(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string sourcePath in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            string name = Path.GetFileName(sourcePath);
            if (name is "steamapps" or "userdata" or "config" or "logs" or "appcache")
                continue;

            string destinationPath = Path.Combine(destinationDirectory, name);
            if (Directory.Exists(sourcePath))
                CopySteamClient(sourcePath, destinationPath);
            else
                File.Copy(sourcePath, destinationPath, overwrite: false);
        }

        string sourceLibraryFolders = Path.Combine(sourceDirectory, "steamapps", "libraryfolders.vdf");
        if (File.Exists(sourceLibraryFolders))
        {
            string destinationSteamApps = Path.Combine(destinationDirectory, "steamapps");
            Directory.CreateDirectory(destinationSteamApps);
            File.Copy(sourceLibraryFolders, Path.Combine(destinationSteamApps, "libraryfolders.vdf"), overwrite: false);
        }
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
