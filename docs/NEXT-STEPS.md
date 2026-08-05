# Where this is up to

Working notes, written down because otherwise they exist only in one conversation.
Last updated 2026-08-04.

## State

The v2 rewrite is complete and running. All ten planned milestones are done, 134 tests pass,
Release builds with no warnings. Everything in the original brief is built: tray app, your own
music, output device picker, per-process silence detection, and turning the television on.

Running config lives in `%APPDATA%\NoSilence\settings.json` and is deliberately sparse — only
values that differ from the defaults, so improved defaults still reach it.

## Verified on real hardware

- Gapless playback, mono/22 k, stereo/44.1 k and stereo/48 k, transitions at exactly 4.000 s.
- TV switched off mid-track: six endpoint notifications in three seconds collapsed into one
  open attempt, audio back **1.7 s** after the endpoint returned, first try, no backoff.
- Missing device: one log line, ~1.4% CPU, steady 5 s re-checks that never escalate.
- Endpoint-ID repair: a deliberately stale ID fell back to the friendly name, picked the
  *active* SAMSUNG rather than one of the three dead ones, and wrote the real ID back.
- Ducking: a separate process at −30 dBFS silenced the music 1.5 s later, correctly
  attributed, and it resumed 20.2 s after that process stopped (release was 20 s then).
- Waking the television: `standby → on` in about two seconds.
- Published single-file binaries run, including `--help` and `--replay`.

## Not verified

- **Sleep/resume recovery.** `PowerModeChanged` → teardown → 3 s settle → reopen is written
  and never exercised. Needs the machine actually suspended.
- **CI and the release workflow.** Cannot run GitHub Actions locally. First push or tag proves it.
- **A real tag build.** `release.yml` has never produced a release.

## Not built

- Crossfade between tracks (`crossfadeMs` is wired through settings but does nothing).
- Per-track manual gain.
- HDMI-CEC provider — a stub only. Consumer NVIDIA cards do not expose CEC; this needs a
  Pulse-Eight adapter, and the shell-command provider already covers those people.

## Things that turned out not to be true

Worth keeping, because each one cost real debugging time and each is written into the design.

1. **"Windows deletes the HDMI audio endpoint when the TV powers off."** True for a remote
   power-off. **False** for a `KEY_POWEROFF` standby, which leaves the HDMI link asserted, so
   Windows keeps the endpoint Active while the screen is dark. Endpoint presence is therefore
   not a trustworthy power sensor on its own; the television's own report wins.
2. **"A Samsung set closes its remote port in standby, so power-on must be Wake-on-LAN."**
   This set (QE75Q7FAAUXXH) keeps 8001, 8002 and 8080 open while asleep and **ignores magic
   packets entirely** — 54 packets from three interfaces, no effect. `KEY_POWER` over the
   WebSocket is what works. Wake still sends Wake-on-LAN first, for sets that behave the
   documented way.
3. **`SHQueryUserNotificationState` catches games.** Only true exclusive full screen and
   presentation mode. Borderless-windowed games — most modern ones — look like ordinary
   windows. The signal is a supplement, nothing more.

## This machine

- Output: `SAMSUNG (NVIDIA High Definition Audio)`,
  `{0.0.0.00000000}.{db81019e-8b55-492e-9c78-dab88006f32f}`. Note there are **four** endpoints
  with that friendly name — one active, three stale — which is why the ID matters.
- Music: `D:\2am.mp3` and `D:\3am.mp3`, scanned non-recursively because `D:\` is 2.8 TB.
- Television: `TV From Hell` (QE75Q7FAAUXXH) at `192.168.1.238`, MAC `4C:57:39:B8:05:5D`
  (ARP and the TV's own report agree, so it is wired). Paired; the token is in `state.json`.
- Three subnets are present — the LAN plus VirtualBox and WSL adapters — which is exactly why
  Wake-on-LAN sends from every interface.
- Automatic TV wake and sleep are **off**. Turn them on in Settings → Television when trusted.

## Next steps, in order

Step 1 has now happened — two days of daily use, written up as discrete items in
`docs/BACKLOG.md`. Calls are the one real problem (NS-1 to NS-3); start there.

1. ~~**Live with it.**~~ Done, 4–5 August. The most valuable input was which behaviours
   irritate, and the answer was unambiguous: 352 play/silence flips in a day, almost all of
   them a video call being sampled as intermittent noise.
2. **Record a normal evening** — and now, more urgently, a call. Five minutes is enough:
   `NoSilence.Console.exe --diagnose --seconds 300 --jsonl session.jsonl`, then
   `--replay session.jsonl`. Tuning can then happen against real data without repeating
   anything. Recordings are moved aside rather than overwritten.
3. **Watch for microphone false positives.** This is the likeliest remaining source of them.
   `SystemSettings.exe` was caught silencing the music three seconds after a real launch, and
   the exclusion list is certainly still incomplete. Applications the rules already ignore are
   now ignored for the microphone too.
4. **Verify sleep/resume** next time the machine is suspended anyway.
5. **Tag `v2.0.0`** once CI has been seen to pass, and check the release workflow produces
   both binaries plus `SHA256SUMS.txt`.
6. **Then**, if wanted: crossfade, per-track gain.

## Testing constraints

The output device is a television that gets watched. Prefer verification that plays nothing:
`--diagnose` observes without playing, and an empty library exercises detection with no audio
at all. Avoid television power commands unless testing them specifically, and put the set back
as it was afterwards.
