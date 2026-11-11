using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImGuiNET;
using Raylib_cs;
using rlImGui_cs;

namespace Steamalt;

internal static partial class Program
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
    private static readonly List<SteamProcessInfo> _runningProcesses = new();
    private static readonly Dictionary<int, SteamProfile> _launchedFirejailProcesses = new();
    private static int _selectedProcessId;

    private static int Main()
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine($"{AppTitle} is supported on Linux only.");
            return 1;
        }

        LoadConfig();
        StartUpdateCheck();
        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.HighDpiWindow);
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
            RefreshRunningProcesses();
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


}
