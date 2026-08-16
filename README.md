# NoSilence

[![CI](https://github.com/BorderKeeper/NoSilence/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/BorderKeeper/NoSilence/actions/workflows/ci.yml)
[![Release](https://github.com/BorderKeeper/NoSilence/actions/workflows/release.yml/badge.svg)](https://github.com/BorderKeeper/NoSilence/actions/workflows/release.yml)
[![Coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fborderkeeper.github.io%2FNoSilence%2Fcoverage.json)](https://github.com/BorderKeeper/NoSilence/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/BorderKeeper/NoSilence?sort=semver&label=latest&color=2f6fe0)](https://github.com/BorderKeeper/NoSilence/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/BorderKeeper/NoSilence/total?color=2f6fe0)](https://github.com/BorderKeeper/NoSilence/releases)
![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white)
![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)

Plays music on a secondary audio output — a TV, a spare speaker — but only while your PC is
otherwise quiet. Start a game, a video or a call and it fades out; stop, and it fades back in.

It lives in the system tray and is meant to be forgotten about.

**[borderkeeper.github.io/NoSilence](https://borderkeeper.github.io/NoSilence/)**

## Getting started

1. Download `NoSilence.exe` from [Releases](../../releases) and run it. It appears in the
   system tray.
2. Right-click the icon → **Settings…**
3. **Library** — add a folder of music.
4. **Output** — pick the device to play on. **Play a test tone** confirms you have the right
   one; if the target is a television, it also tells you whether it is on the right input.

Every device carries its own Windows volume, and a television's habitually comes back muted
after the set has been switched off, which makes the app report *Playing* into a silent room. So
a mute — or a volume below 2% — is cleared as NoSilence opens the device. Muting it while the
music is playing still means what you meant by it.

That is the whole setup. Everything else has sensible defaults.

There is no installer and the binary is not code-signed, so SmartScreen will warn on first
run: *More info → Run anyway*.

## How it decides to go quiet

It enumerates Windows audio sessions **per process**, so it knows *which application* is
making sound rather than merely that something is. Its own playback is excluded by process ID,
which is why the music output is allowed to be the same device you listen on.

A source has to stay consistently above about −50 dBFS for two seconds before the music fades
out, and everything has to stay quiet for five seconds before it comes back. Those numbers are
adjustable, and the sustain requirement exists because notification chimes are around a second
long — without it, a single Discord ping buys you a stretch of silence.

Per-application rules refine it: chat clients need four seconds (a ping is short, a call is
not), browsers need 2.5, media players trigger instantly, and console windows never count
because they only ever ring a bell.

Right-click any row in **What's playing** to change how an application is treated.

See [docs/DETECTION.md](docs/DETECTION.md) for the details, and for how to tune it against a
recording of your own machine rather than by guessing.

## Turning the television on

Optional and off by default — see [docs/TV.md](docs/TV.md). A PC graphics card cannot send
HDMI-CEC, so this happens over the network: Wake-on-LAN, the Samsung remote protocol, or any
command you care to configure.

Once a television is configured, NoSilence turns it on shortly after it starts, so logging on is
enough to get the room going. Unless you switched the set off by hand within the last hour, in
which case it leaves it alone.

## Two executables

| | |
|---|---|
| `NoSilence.exe` | The tray app. Run this normally. |
| `NoSilence.Console.exe` | The same program, console subsystem. Use it for anything that prints. |

A Windows program is either GUI or console, never both. The tray app must be the former or a
console window flashes at every logon — but that also means a shell will not wait for it or
show its output, so the diagnostic commands get their own binary.

```
NoSilence.Console.exe --list-devices     list audio outputs with their endpoint IDs
NoSilence.Console.exe --diagnose         watch what every application is playing, live
NoSilence.Console.exe --replay FILE      re-run a recording through the current settings
NoSilence.Console.exe --discover-tv      find Samsung televisions on the network
NoSilence.Console.exe --wake-tv          turn the configured television on
NoSilence.exe --quit                     stop a running instance cleanly
NoSilence.exe --help                     everything else
```

## Where things are kept

`%APPDATA%\NoSilence\` — `settings.json`, `state.json` and `logs\`.

`settings.json` records only what differs from the defaults, so an improved default still
reaches you instead of being pinned by a file written on first exit.

Put a file named `nosilence.portable` beside the executable and it keeps everything in
`NoSilenceData\` next to itself instead.

## Building

```
dotnet build NoSilence.sln
dotnet test  NoSilence.sln
dotnet test  NoSilence.sln --collect:"XPlat Code Coverage"
```

Requires the .NET 9 SDK. Windows only — it is built on WASAPI throughout.

The coverage badge is line coverage over the whole assembly, so the settings UI, the WASAPI
interop and the process entry points are all in the denominator at zero, and will stay there.
The detection logic the tests exist for sits far above the headline figure; playback and the
television code sit below it. CI writes the number to `coverage.json` on the site after every
run on `master`, and the badge reads it from there — no coverage service is involved.
