using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImGuiNET;
using Raylib_cs;
using rlImGui_cs;

namespace Steamalt;

public sealed class SteamProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Profile";
    public string GameAppId { get; set; } = "438100";
}

public sealed class LauncherConfig
{
    public List<SteamProfile> Profiles { get; set; } = new();
}

internal static class Program
{
    private const string AppTitle = "Steamalt";
    private const string FirejailExecutable = "firejail";
    private const string SteamExecutable = "steam";
    private static readonly string HostHomeDirectory =
        Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string ConfigDirectory = GetXdgDirectory("XDG_CONFIG_HOME", ".config");
    private static readonly string DataDirectory = GetXdgDirectory("XDG_DATA_HOME", ".local/share");
    private static readonly string ConfigPath = Path.Combine(ConfigDirectory, "steamalt", "config.json");
    private static readonly string ProfilesDirectory = Path.Combine(DataDirectory, "steamalt", "profiles");
    private static readonly Vector4 ErrorColor = new(1f, 0.35f, 0.35f, 1f);

    private static LauncherConfig _config = new();
    private static string _status = "Ready.";
    private static bool _isError;

    private static int Main()
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine($"{AppTitle} is supported on Linux only.");
            return 1;
        }

        LoadConfig();
        Raylib.InitWindow(720, 580, AppTitle);
        Raylib.SetTargetFPS(60);
        LoadIcon();
        rlImGui.Setup(true);
        ImGui.StyleColorsDark();

        while (!Raylib.WindowShouldClose())
        {
            Raylib.BeginDrawing();
            Raylib.ClearBackground(new Color(28, 28, 34, 255));
            rlImGui.Begin();
            DrawUI();
            rlImGui.End();
            Raylib.EndDrawing();
        }

        SaveConfig();
        rlImGui.Shutdown();
        Raylib.CloseWindow();
        return 0;
    }

    private static string GetXdgDirectory(string variable, string fallback)
    {
        string? configured = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            fallback);
    }

    private static void LoadIcon()
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
        if (!File.Exists(iconPath))
            return;

        Image icon = Raylib.LoadImage(iconPath);
        Raylib.SetWindowIcon(icon);
        Raylib.UnloadImage(icon);
    }

    private static void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                LauncherConfig? loaded = JsonSerializer.Deserialize<LauncherConfig>(
                    File.ReadAllText(ConfigPath));
                if (loaded is null || loaded.Profiles is null)
                    throw new InvalidDataException("The config does not contain a valid profiles list.");

                _config = loaded;
            }
            else
            {
                _config.Profiles.Add(new SteamProfile { Name = "Main" });
            }
        }
        catch (Exception ex)
        {
            _status = $"Could not load config: {ex.Message}";
            _isError = true;
            _config.Profiles.Clear();
            _config.Profiles.Add(new SteamProfile { Name = "Main" });
        }
    }

    private static bool SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            _status = $"Could not save config: {ex.Message}";
            _isError = true;
            return false;
        }
    }

    private static void DrawUI()
    {
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(Raylib.GetScreenWidth(), Raylib.GetScreenHeight()));
        ImGui.Begin(AppTitle, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse);

        ImGui.TextWrapped("Launch Steam games through Firejail using the host Steam installation and game libraries.");
        ImGui.Dummy(new Vector2(0, 8));

        DrawProfiles();

        ImGui.Dummy(new Vector2(0, 8));
        if (_isError)
            ImGui.TextColored(ErrorColor, _status);
        else
            ImGui.TextDisabled(_status);

        ImGui.End();
    }

    private static void DrawProfiles()
    {
        ImGui.Text("Launch profiles");
        ImGui.SameLine();
        if (ImGui.Button("Add profile"))
            AddProfile();

        ImGui.BeginChild("Profiles", new Vector2(0, 390), ImGuiChildFlags.Borders);
        for (int i = 0; i < _config.Profiles.Count; i++)
        {
            SteamProfile profile = _config.Profiles[i];
            ImGui.PushID(profile.Id.ToString("N"));

            ImGui.TextDisabled("Profile");
            ImGui.SameLine();
            string name = profile.Name;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            if (ImGui.InputText("##name", ref name, 64))
            {
                profile.Name = name;
                SaveConfig();
            }

            ImGui.TextDisabled("Game App ID");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110);
            string gameAppId = profile.GameAppId;
            if (ImGui.InputText("##gameappid", ref gameAppId, 16))
            {
                profile.GameAppId = gameAppId;
                SaveConfig();
            }

            ImGui.SameLine();
            if (ImGui.Button("Launch game", new Vector2(105, 0)))
                LaunchProfile(profile, launchGame: true);
            ImGui.SameLine();
            if (ImGui.Button("Start Steam", new Vector2(95, 0)))
                LaunchProfile(profile, launchGame: false);
            ImGui.SameLine();
            if (ImGui.Button("Remove", new Vector2(75, 0)))
            {
                _config.Profiles.RemoveAt(i);
                if (SaveConfig())
                {
                    _status = "Removed launch profile. Its Firejail data was kept.";
                    _isError = false;
                }
                ImGui.PopID();
                break;
            }

            ImGui.PopID();
            ImGui.Separator();
        }
        ImGui.EndChild();
    }

    private static void AddProfile()
    {
        int number = 1;
        string name;
        do
        {
            name = $"Profile {number++}";
        } while (_config.Profiles.Any(profile => profile.Name == name));

        _config.Profiles.Add(new SteamProfile { Name = name });
        if (SaveConfig())
        {
            _status = $"Added profile {name}.";
            _isError = false;
        }
    }

    private static void LaunchProfile(SteamProfile profile, bool launchGame)
    {
        string appId = profile.GameAppId.Trim();
        if (launchGame && (!uint.TryParse(appId, out uint parsedAppId) || parsedAppId == 0))
        {
            _status = $"Enter a valid Steam Game App ID for {profile.Name}.";
            _isError = true;
            return;
        }

        try
        {
            string profileHome = Path.Combine(ProfilesDirectory, profile.Id.ToString("N"));
            Directory.CreateDirectory(profileHome);
            string steamRoot = FindSteamRoot();
            IReadOnlyList<string> steamLibraries = FindSteamLibraries(steamRoot);
            EnsureSteamInstallLinks(profileHome, steamRoot);

            var startInfo = CreateFirejailStartInfo(profileHome, steamRoot, steamLibraries);
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(SteamExecutable);
            if (launchGame)
            {
                startInfo.ArgumentList.Add("-applaunch");
                startInfo.ArgumentList.Add(appId);
            }

            using Process? process = Process.Start(startInfo);
            if (process is null)
                throw new InvalidOperationException("The operating system did not start Firejail.");

            _status = launchGame
                ? $"Started Steam to launch App ID {appId} for {profile.Name}."
                : $"Started Steam for {profile.Name}.";
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
        IReadOnlyList<string> steamLibraries)
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
        startInfo.ArgumentList.Add($"--env=XDG_DATA_HOME={DataDirectory}");
        startInfo.ArgumentList.Add($"--env=XDG_CACHE_HOME={Path.Combine(profileHome, ".cache")}");
        startInfo.ArgumentList.Add($"--env=XDG_STATE_HOME={Path.Combine(profileHome, ".local", "state")}");
        startInfo.ArgumentList.Add($"--read-only={HostHomeDirectory}");
        startInfo.ArgumentList.Add($"--read-write={profileHome}");
        startInfo.ArgumentList.Add($"--read-write={steamRoot}");
        foreach (string library in steamLibraries)
            startInfo.ArgumentList.Add($"--read-write={library}");
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

    private static void EnsureSteamInstallLinks(string profileHome, string steamRoot)
    {
        string steamConfigDirectory = Path.Combine(profileHome, ".steam");
        Directory.CreateDirectory(steamConfigDirectory);

        foreach (string linkName in new[] { "steam", "root" })
        {
            string linkPath = Path.Combine(steamConfigDirectory, linkName);
            var linkInfo = new DirectoryInfo(linkPath);
            string? linkTarget = linkInfo.LinkTarget;
            if (linkTarget is not null)
            {
                string currentTarget = linkInfo.ResolveLinkTarget(true)?.FullName
                    ?? Path.GetFullPath(
                        Path.IsPathRooted(linkTarget)
                            ? linkTarget
                            : Path.Combine(steamConfigDirectory, linkTarget));
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
