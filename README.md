# Steamalt

Steamalt is a Linux-only launcher for starting Steam and Steam games through Firejail. Each named launch profile has a persistent, separate copy of the Steam client data and its own Steam login, while using the host game libraries.

## Requirements

- Linux (x64 or ARM64)
- .NET 10
- Steam and Firejail installed and available in `PATH`
- Steam installed on the host, with games installed in its libraries

Steamalt does not install or update Steam or games. The first launch copies the host Steam client files and library index into the profile; after that, each profile has its own Steam login, client state, shortcuts, and Proton compatibility data. Installed game libraries remain shared. Host-installed Proton and other compatibility tools are exposed to each profile read-only through `STEAM_COMPAT_TOOL_PATHS`, including tools installed in registered Steam libraries; each profile keeps its own prefixes and game state.

## Usage

Add a launch profile and select **Start Steam** to open that profile's separate Steam client inside Firejail. Sign in to the desired account and launch games from that Steam window. Profiles use stable homes under `$XDG_DATA_HOME/steamalt/profiles` (or `~/.local/share/steamalt/profiles` when `XDG_DATA_HOME` is unset). Removing a profile does not delete its profile home.

To use a non-Steam game, select **Start Steam**, then add it in that profile's Steam window using **Games > Add a Non-Steam Game to My Library**. Each profile stores its own non-Steam shortcuts. The host home and game libraries are visible to Steam; the host files remain read-only.

**Start Steam** runs:

```text
firejail --ignore=seccomp --ignore=whitelist --ignore=blacklist \
  --profile=steam --allow-bwrap \
  --env=HOME=<profile-home> \
  --read-only=<host-home> --read-write=<profile-home> \
  --read-write=<profile-steam-install> --read-write=<steam-library> \
  -- steam
```

Steamalt disables Firejail's seccomp filter so Proton's pressure-vessel `bwrap` can use `mount_setattr`, and enables nested bwrap. It ignores the Steam profile's whitelist and blacklist rules, which otherwise make host game paths such as `~/Applications` inaccessible to Steam. The host home and host Steam installation are read-only. The profile's copied Steam installation and registered game libraries are writable. Launch games and non-Steam games from the Steam window opened by Steamalt.

Steamalt uses the `firejail` and `steam` executables from `PATH`. Profiles are saved to `$XDG_CONFIG_HOME/steamalt/config.json` (or `~/.config/steamalt/config.json` when `XDG_CONFIG_HOME` is unset).

## Updates

When running as an AppImage, Steamalt checks the latest GitHub release on startup. If the release has a newer SemVer version than the current build, it downloads the matching architecture AppImage, replaces the current file, and restarts automatically. Updates are not applied when running the unpackaged executable; download the latest AppImage from the [Steamalt releases](https://github.com/hollyntt/Steamalt/releases) page.

## Build

```sh
dotnet publish Steamalt.csproj -c Release -r linux-x64 --self-contained false
```

Use `-r linux-arm64` to publish for ARM64 Linux.
