using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;

namespace Steamalt;

internal static partial class Program
{
    private static readonly HttpClient UpdateClient = CreateUpdateClient();
    private static int _updateCheckStarted;
    private static string _updateStatus = "Updates have not been checked.";
    private static bool _updateInProgress;

    private static HttpClient CreateUpdateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Steamalt", GetCurrentVersion()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string GetCurrentVersion()
    {
        return Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static void StartUpdateCheck()
    {
        if (Interlocked.Exchange(ref _updateCheckStarted, 1) == 0)
            _ = CheckForUpdatesAndApplyAsync();
    }

    private static async Task CheckForUpdatesAndApplyAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPIMAGE")))
        {
            _updateStatus = "Auto-updates are available when running the AppImage.";
            return;
        }

        _updateStatus = "Checking for updates...";
        try
        {
            using JsonDocument release = await GetLatestReleaseAsync();
            JsonElement root = release.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            if (!IsNewerVersion(tag, GetCurrentVersion()))
            {
                _updateStatus = $"Up to date ({tag}).";
                return;
            }

            string assetName = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "Steamalt-linux-arm64.AppImage"
                : "Steamalt-linux-x64.AppImage";
            string? downloadUrl = FindAssetUrl(root, assetName);
            if (downloadUrl is null)
            {
                _updateStatus = $"Update {tag} has no {assetName} asset.";
                return;
            }

            _updateStatus = $"Applying update {tag}...";
            await ApplyAppImageUpdateAsync(downloadUrl);
        }
        catch (Exception ex)
        {
            _updateStatus = $"Update check failed: {ex.Message}";
        }
    }

    private static async Task<JsonDocument> GetLatestReleaseAsync()
    {
        const string endpoint = "https://api.github.com/repos/hollyntt/Steamalt/releases/latest";
        using HttpResponseMessage response = await UpdateClient.GetAsync(endpoint);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static string? FindAssetUrl(JsonElement release, string assetName)
    {
        if (!release.TryGetProperty("assets", out JsonElement assets))
            return null;

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (string.Equals(asset.GetProperty("name").GetString(), assetName, StringComparison.OrdinalIgnoreCase))
                return asset.GetProperty("browser_download_url").GetString();
        }

        return null;
    }

    private static bool IsNewerVersion(string remoteTag, string localVersion)
    {
        string remoteCore = remoteTag.TrimStart('v', 'V').Split('-', '+')[0];
        string localCore = localVersion.TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(remoteCore, out Version? remote)
            && Version.TryParse(localCore, out Version? local)
            && remote > local;
    }

    private static async Task ApplyAppImageUpdateAsync(string downloadUrl)
    {
        if (_updateInProgress)
            return;
        _updateInProgress = true;

        string? currentAppImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrWhiteSpace(currentAppImage))
            throw new InvalidOperationException("The current process is not running from an AppImage.");

        string temporaryPath = Path.Combine(Path.GetTempPath(), $"Steamalt-update-{Environment.ProcessId}.AppImage");
        string backupPath = currentAppImage + ".bak";
        try
        {
            using HttpResponseMessage response = await UpdateClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (Stream source = await response.Content.ReadAsStreamAsync())
            await using (FileStream destination = File.Create(temporaryPath))
                await source.CopyToAsync(destination);

            File.Move(currentAppImage, backupPath, overwrite: true);
            File.Move(temporaryPath, currentAppImage);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(currentAppImage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            Process.Start(new ProcessStartInfo(currentAppImage) { UseShellExecute = true });
            Environment.Exit(0);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            if (!File.Exists(currentAppImage) && File.Exists(backupPath))
                File.Move(backupPath, currentAppImage);
            throw;
        }
    }
}
