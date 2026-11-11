using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using Raylib_cs;

namespace Steamalt;

internal static partial class Program
{
    private static void DrawUI()
    {
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(Raylib.GetScreenWidth(), Raylib.GetScreenHeight()), ImGuiCond.Always);
        ImGui.Begin(AppTitle, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove);

        ImGui.TextWrapped("Run separate Steam accounts through Firejail and monitor which account owns each Steam process.");
        ImGui.Dummy(new Vector2(0, 8));

        DrawProfiles();
        DrawProcessManager();

        ImGui.Dummy(new Vector2(0, 8));
        if (_isError)
            ImGui.TextColored(ErrorColor, _status);
        else
            ImGui.TextDisabled(_status);

        ImGui.End();
    }

    private static void DrawProcessManager()
    {
        const float processManagerHeight = 180f;
        ImGui.Dummy(new Vector2(0, 8));
        ImGui.Text($"Account process manager ({_runningProcesses.Count} active)");
        ImGui.BeginChild("Processes", new Vector2(0, processManagerHeight), ImGuiChildFlags.Borders);
        if (_runningProcesses.Count == 0)
        {
            ImGui.TextDisabled("No Steam processes launched by Steamalt are running.");
        }
        else
        {
            foreach (SteamProcessInfo process in _runningProcesses)
            {
                ImGui.PushID(process.ProcessId);
                bool selected = ImGui.Selectable(
                    $"{process.ProfileName}  |  PID {process.ProcessId}  |  {process.Status}",
                    _selectedProcessId == process.ProcessId);
                if (selected)
                    _selectedProcessId = process.ProcessId;
                ImGui.SameLine();
                DrawProcessActions(process);
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
    }

    private static void DrawProcessActions(SteamProcessInfo process)
    {
        if (ImGui.SmallButton("Focus"))
            FocusProcess(process.ProcessId);
        ImGui.SameLine();
        if (ImGui.SmallButton("Stop"))
            StopProcess(process.ProcessId, force: false);
        ImGui.SameLine();
        if (ImGui.SmallButton("Kill"))
            StopProcess(process.ProcessId, force: true);
        ImGui.SameLine();
        if (ImGui.SmallButton("Restart"))
        {
            StopProcess(process.ProcessId, force: false);
            SteamProfile? profile = _config.Profiles.FirstOrDefault(candidate => candidate.Id == process.ProfileId);
            if (profile is not null)
                LaunchProfile(profile);
        }
    }

    private static void DrawProfiles()
    {
        ImGui.Text("Launch profiles");
        ImGui.SameLine();
        if (ImGui.Button("Add profile"))
            AddProfile();

        float processManagerHeight = 180f;
        float profilesHeight = MathF.Max(120f, ImGui.GetContentRegionAvail().Y - processManagerHeight - 55f);
        ImGui.BeginChild("Profiles", new Vector2(0, profilesHeight), ImGuiChildFlags.Borders);
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

            if (ImGui.Button("Start Steam", new Vector2(95, 0)))
                LaunchProfile(profile);
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

    private static void RefreshRunningProcesses()
    {
        foreach (int processId in _launchedFirejailProcesses.Keys.ToArray())
        {
            if (!Directory.Exists($"/proc/{processId}"))
                _launchedFirejailProcesses.Remove(processId);
        }

        _runningProcesses.Clear();
        if (!Directory.Exists("/proc"))
            return;

        foreach ((int processId, SteamProfile profile) in _launchedFirejailProcesses)
        {
            if (Directory.Exists($"/proc/{processId}"))
            {
                _runningProcesses.Add(new SteamProcessInfo
                {
                    ProcessId = processId,
                    ProfileId = profile.Id,
                    ProfileName = profile.Name,
                    Status = "Firejail session"
                });
            }
        }

        foreach (string processDirectory in Directory.EnumerateDirectories("/proc"))
        {
            string processIdText = Path.GetFileName(processDirectory);
            if (!int.TryParse(processIdText, out int processId))
                continue;

            try
            {
                string commandLine = ReadProcText(Path.Combine(processDirectory, "cmdline"));
                if (!commandLine.Contains("steam", StringComparison.OrdinalIgnoreCase))
                    continue;

                string environment = ReadProcText(Path.Combine(processDirectory, "environ"));
                SteamProfile? profile = FindProfileForProcess(processId, environment);
                if (profile is null)
                    continue;

                if (!_runningProcesses.Any(existing => existing.ProcessId == processId))
                {
                    _runningProcesses.Add(new SteamProcessInfo
                    {
                        ProcessId = processId,
                        ProfileId = profile.Id,
                        ProfileName = profile.Name,
                        Status = commandLine.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Steam"
                    });
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _runningProcesses.Sort((left, right) =>
        {
            int profileComparison = string.Compare(left.ProfileName, right.ProfileName, StringComparison.OrdinalIgnoreCase);
            return profileComparison != 0
                ? profileComparison
                : left.ProcessId.CompareTo(right.ProcessId);
        });
    }

    private static SteamProfile? FindProfileForProcess(int processId, string environment)
    {
        SteamProfile? profile = _config.Profiles.FirstOrDefault(candidate =>
            environment.Contains(
                $"HOME={Path.Combine(ProfilesDirectory, candidate.Id.ToString("N"))}",
                StringComparison.Ordinal));
        if (profile is not null)
            return profile;

        if (_launchedFirejailProcesses.TryGetValue(processId, out profile))
            return profile;

        try
        {
            using Process process = Process.GetProcessById(processId);
            int parentId = ReadParentProcessId(processId);
            if (_launchedFirejailProcesses.TryGetValue(parentId, out profile))
                return profile;
        }
        catch (ArgumentException)
        {
        }
        catch (IOException)
        {
        }

        return null;
    }

    private static int ReadParentProcessId(int processId)
    {
        string status = File.ReadAllText($"/proc/{processId}/status");
        string? parentLine = status.Split('\n').FirstOrDefault(line => line.StartsWith("PPid:", StringComparison.Ordinal));
        return parentLine is not null && int.TryParse(parentLine[5..].Trim(), out int parentId) ? parentId : -1;
    }

    private static string ReadProcText(string path)
    {
        return File.ReadAllText(path).Replace('\0', '\n');
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

}
