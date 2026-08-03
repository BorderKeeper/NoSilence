# NoSilence

Plays music on a secondary audio output — a TV, a spare speaker — but only while your PC is
otherwise quiet. Start a game, a video or a call and it fades out; stop, and it fades back in.

It lives in the system tray and is meant to be forgotten about.

## Getting started

1. Download `NoSilence.exe` from [Releases](../../releases) and run it. It appears in the
   system tray.
2. Right-click the icon → **Settings…**
3. **Library** — add a folder of music.
4. **Output** — pick the device to play on. **Play a test tone** confirms you have the right
   one; if the target is a television, it also tells you whether it is on the right input.

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
```

Requires the .NET 9 SDK. Windows only — it is built on WASAPI throughout.
