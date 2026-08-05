# Backlog — from two days of daily use

Field notes from 4–5 August 2026, the first real "live with it" period asked for in
`NEXT-STEPS.md`. Each item is written so someone else can pick it up cold: what was observed,
what the log says, where the code is, and what "fixed" looks like.

## What the two days actually showed

One theme dominates. **Video calls are the failure case**, and everything else is minor.

Counted from `%APPDATA%\NoSilence\logs\nosilence-20260805.log`:

| | |
|---|---|
| Silence events attributed to `Zoom.exe` (output) | 959 |
| Silence events attributed to `Zoom.exe (microphone)` | 623 |
| Resumes (`PLAYING`) | 352 |
| Flap warnings logged | 13 (and 9 the previous day) |

352 play/silence flips in one day. The user's own summary — *"13:46 every time I silenced the
app was because I was on a call and music started playing"* — matches the data exactly. The
detection is not too insensitive or too sensitive; it is applying a 5-second release to a
context that lasts an hour.

Everything else works. The full-screen game case, the one `NEXT-STEPS.md` was pessimistic
about, was caught correctly and released correctly.

---

## NS-1 — Calls flap: the music returns in every conversational pause

**Done — 5 August, unverified against real hardware.** Implemented as a call *hold* rather than
a longer release: see `CaptureMode.Call` in `ProcessRule.cs` and `EvaluateCaptureSessions` in
`DecisionEngine.cs`. Eight tests cover it. Still needs a real call to confirm — NS-10.

**Priority: high.** This is the headline bug and NS-2, NS-3 and NS-9 are all downstream of it.

**Observed.** 10:24–10:26, *"stopped when I was on a zoom call for no reason, no one was
talking, twice"*. 11:37, *"left meeting and music came back (and cut off again as I was typing
this out)"*.

**Evidence.** Nine `SILENT`→`PLAYING`→`SILENT` cycles in the seven minutes from 10:23:55 to
10:30:45 on 4 August. The shape repeats all day:

```
10:25:41  SILENT   Zoom.exe (microphone): in use, peaked at -18.2 dBFS
10:25:43  SILENT   Quiet — resuming in 5 s
10:25:46  SILENT   Zoom.exe (microphone): in use, peaked at -18.7 dBFS
10:25:49  SILENT   Quiet — resuming in 5 s
10:25:54  PLAYING  Nothing else is playing
10:26:23  SILENT   Zoom.exe (microphone): in use, peaked at -5.3 dBFS
```

At 11:04:30 the music came up for **751 ms** mid-call before being cut again.

**Diagnosis.** `DecisionEngine.MaybeRelease` (`src/NoSilence/Detection/DecisionEngine.cs:293`)
applies one flat `ReleaseMs` — 5 s, `DetectionConfig.cs:64` — to every trigger. A call is a
single continuous context that is *sampled* as intermittent noise: Zoom's render session goes
quiet between sentences and the capture session goes quiet whenever the user stops speaking.
Five seconds of quiet inside a call is not the call ending, it is a pause for breath. The 20 s
release this shipped with would have masked the problem; 5 s exposes it.

**What was built.** A call context that outlives a single quiet window. Silence is held for as
long as the conferencing application keeps its capture session open, and only then does a
release timer start — `CallReleaseMs`, 15 s, against 5 s for everything else. Ordinary render
triggers are untouched.

Entirely inside the pure engine, so it replays: capture-session state was already on the
snapshot and no new I/O was needed.

**Acceptance.** `ACallProducesExactlyTwoTransitions` drives five simulated minutes of
alternating speech and silence plus the meeting ending, and asserts exactly two transitions.
Still to do: replay a real recording (NS-10) and confirm the same shape against real data.

---

## NS-2 — The microphone signal measures your voice, not whether you are in a call

**Done — 5 August, unverified.** `CaptureMode` on `ProcessRule`, defaulting to `Level`, set to
`Call` for the conferencing applications. Zoom was missing from the rules table entirely and is
now in it.

**Priority: high.** Fix alongside NS-1.

**Observed.** *"Seems like it's me talking or typing on the keyboard, not sure if that is
necessary tbh"* (10:24) and, the next morning, *"Yep zoom.exe (microphone) is my microphone so
talking while on zoom shuts down the music"* (09:59).

**Evidence.** The logged capture peaks are the user's own voice hitting the input — `0.0 dBFS`
at 11:03:37 and 11:04:57, `-1.2 dBFS` at 11:02:08. Between utterances the level collapses and
the release timer starts.

**Diagnosis.** `DecisionEngine.EvaluateCaptureSessions`
(`src/NoSilence/Detection/DecisionEngine.cs:159`) tests capture *level* against
`MicThresholdDb` (−45) with a 3 s sustain. Level is the wrong axis. The documented intent —
`DETECTION.md`: *"catches calls even when the other end is quiet"* — is about the microphone
being **open**, not about how loud you are. Measuring loudness turns one call into one duck per
sentence.

`TreatActiveCaptureAsNoise` (`DetectionConfig.cs:111`) is already the right primitive and is
correctly off by default, because globally it would trip on OBS and Voicemeeter holding a
permanent capture session.

**What was built.** Per-rule rather than global, and deliberately *not* the obvious version.
"Capture session active means silence" is what `TreatActiveCaptureAsNoise` already does, and it
is off by default because OBS and Voicemeeter hold a session open forever. So the call **arms**
on sustained microphone signal — exactly the condition that used to trigger a duck directly —
and only then **holds** while the session stays open. Nothing new starts ducking; only the
stopping changed. A two-minute idle timeout bounds the hold so a client that keeps the
microphone open after a meeting cannot strand the music.

**Still open, worth measuring.** Does keyboard noise alone cross −45 dBFS on this desk mic for
3 s? If it does, typing near an open Zoom microphone can now arm a call rather than cause a
single duck — better than the old behaviour, but still wrong. A `--diagnose` recording of
typing with no meeting running settles it in a minute. If it turns out to be true, raising
`MicThresholdDb` for call applications specifically is the fix, and the plumbing for a
per-rule threshold is already there.

---

## NS-3 — Call mode, as a feature rather than a workaround

**Priority: high.** Asked for directly: *"10:29 seems like on a call it can be annoying, some
heuristic would be nice."*

**Evidence that the current answer is a workaround.** Snooze was used seven times across the
two days — 10:30, 11:06, 12:25, 12:40, 13:15, 13:46, 15:33 — each time for 15–60 minutes, each
time because a call was starting. *"Snoozed it for 30 minutes to avoid starting and stopping on
a call."*

**What to build.** A first-class call state, on top of NS-1 and NS-2:

- ~~Entering: one transition into silence, logged once, not once per sentence.~~ Done via NS-1.
- ~~While in it: the reason reads *"In a call — Zoom"* rather than a dBFS figure.~~ Done — the
  tray tooltip, the menu's "why" line and the log all take their text from
  `DecisionOutcome.Reason`, so all three changed together.
- ~~Leaving: capture session closes → hold, then one resume.~~ Done via NS-1.
- ~~The escape hatch.~~ Done. `OverrideState.PlayThroughCall`, offered two ways: a **Play
  through this call** menu item visible only during a call, and a balloon when a call starts
  whose body is itself the button. It suppresses both the microphone *and* the call
  application's own audio — suppressing only the microphone would leave the other end talking
  still ducking the music — and resumes immediately rather than waiting out the release,
  because an explicit click means now.

  It expires when the call ends, cleared in `DetectionService.Tick`. That is the whole point:
  an override that outlived its call would be indistinguishable from the microphone signal
  being switched off, and would be discovered the same way — days later, by accident.

**Acceptance.** A full working day with a call in it produces single-digit transitions and no
manual snoozes.

---

## NS-4 — The output endpoint gets muted by Windows and only the log finds out

**Done — 5 August, unverified.** `PlaybackEngine.MakeOutputAudible`, reachable from a menu item
that appears only when it is needed and from clicking the balloon itself.

**Correction to the original write-up below.** It claimed the tray never surfaced this. That was
wrong: `Apply` already promoted the warning above the phase, set the Error icon, and balloons at
every notification level. What was actually missing was anywhere to *click* — the fix lives in a
per-device Windows volume slider most people have never opened. That is what was added.

**Priority: medium.** Happens **daily**, by the user's account.

**Observed.** 09:22, *"noticed Samsung was muted, which btw happens daily — Windows overnight
mutes my Samsung even though I just turned the TV off at night and switched to headphones in
the morning after PC wakeup."*

**Evidence.**

```
2026-08-04 21:36:23 [WRN] NoSilence.Playback.PlaybackEngine:
    SAMSUNG (NVIDIA High Definition Audio) is muted in Windows, so nothing will be heard.
```

The app knew, twelve hours before the user noticed — but at 21:36 nobody was at the machine to
see the balloon, and by morning there was nothing left but the icon.

**What was built.** A "Make SAMSUNG audible again" item, which clears the mute and lifts a
near-zero volume to 20%. It appears in the tray menu only while the output is inaudible, and
the balloon now carries the same action, so the notification you actually see at the moment it
happens is itself the button.

**Still open.**

1. Unmuting automatically when the endpoint is reopened, behind a setting defaulting to off.
   Deliberately not done yet: changing system volume without being asked wants watching first.
2. The cause. It correlates with the TV being powered off at night and the HDMI endpoint
   re-enumerating. If the NVIDIA HDMI endpoint reliably comes back muted after a power cycle,
   that belongs in `TV.md` whatever else is done about it.

---

## NS-5 — The tray menu takes a visible moment to open

**Done — 5 August, unverified.** Both submenus now fill on their own `DropDownOpening` rather
than when the root menu opens, taking the endpoint enumeration off the path between the
right-click and the menu appearing.

**Observed.** 12:25, *"right clicking takes time for the menu to open."*

**Diagnosis.** `_menu.Opening` (`src/NoSilence/Ui/TrayApplicationContext.cs:62`) runs
`RefreshMenu` synchronously on the UI thread, and `RefreshDeviceMenu`
(`TrayApplicationContext.cs:286`) calls `_app.ListOutputDevices()` → `DeviceCatalog.List` with
`Active | Unplugged | NotPresent`. That is a full COM enumeration of every render endpoint the
machine has ever seen — on this machine, four `SAMSUNG` endpoints alone, three of them stale
(`NEXT-STEPS.md`, "This machine"). `RefreshTvMenu` then rebuilds the television submenu and
reads `_tv.Status` on the same thread.

**What was built.** The simpler half of the suggested fix, which turned out to be the whole of
it: both submenus populate on their own `DropDownOpening`. `RefreshMenu` now does nothing more
expensive than reading a property. Almost nobody opens either submenu, so the enumeration went
from every right-click to approximately never.

Caching the list and refreshing it from `EndpointNotificationBridge` was not done — it only
buys anything once the submenu is already open, which is now the rare path. Worth revisiting
only if opening **Output device** itself feels slow.

**Acceptance.** Not yet measured. A stopwatch around `RefreshMenu` logged at debug would
confirm it; the honest position is that the expensive call is provably no longer on that path.

---

## NS-6 — "Windows toast skipped playback" — needs a reproduction

**Priority: low, but do not lose it.**

**Observed.** 10:30, *"windows toast skipped playback."*

**Evidence.** None that matches. The two ducks either side of that minute are both
`Zoom.exe (microphone)`, at 10:30:20 and 10:30:39. Nothing in the log attributes anything to a
notification.

**Reading of it.** Either the toast caused a *track* skip rather than a duck — a different
subsystem entirely, `PlaylistSampleProvider` / `PlaybackEngine` — or a duck was misattributed
in the tray while the log recorded the real cause. The two need different fixes, so this needs
a reproduction before anyone writes code.

**How to reproduce.** `--diagnose --seconds 120 --jsonl toast.jsonl`, fire a few toasts
deliberately, replay. The 2000 ms sustain exists specifically to swallow a ~1 s chime
(`DetectionConfig.cs:49`); if a toast does duck the music, that is a regression and deserves a
test pinning it.

---

## NS-7 — Confirmed working: full-screen games

No action. Recording it because `NEXT-STEPS.md` is pessimistic about this path and the
pessimism turned out to be too strong.

```
11:40:48  SILENT   Full screen: Busy application in the foreground
11:41:17  SILENT   Quiet — resuming in 5 s
11:41:22  PLAYING  Nothing else is playing
```

*"11:40 launched a full screen game, music stopped (ok). Exited 11:41, music resumed."* The
game registered as `Busy` rather than `FullScreenD3D`, which is why it was caught. Worth adding
`Busy` to the documented list of states that work in `DETECTION.md`, and worth a note that the
borderless-window caveat, while true, is not the whole story.

---

## NS-8 — Television was off in the morning

**Priority: low. Needs a decision more than code.**

09:21, *"TV was off, I turned it off, so I turned it back on."* Automatic TV wake and sleep are
off by default (`NEXT-STEPS.md:69`), so this is the configured behaviour, not a fault.

The decision: should playback starting after a machine wake offer to turn the television on —
a balloon with an action rather than doing it silently? Given `TvPolicy` already has a user-veto
concept for the opposite direction, the asymmetry is a bit odd.

---

## NS-9 — The flap warning fires into the void

**Done — 5 August, unverified.** `DetectionService.Flapping` is raised alongside the log line
and `TrayApplicationContext.NotifyFlapping` turns it into a balloon that opens **What's
playing**. At most one an hour, because that is how often the underlying warning can fire.

**Priority: low. Cheap.**

Twenty-two flap warnings across the two days (9 + 13), every one of them log-only. The user
found the flapping by living through it instead.

```
[WRN] Play/silence has flipped 20 times in the last hour, which suggests the detection
      threshold or a rule needs adjusting. Try --diagnose.
```

Surface it once per hour as a balloon that opens **What's playing** — the view exists and is
exactly the right destination. `DecisionEngine.EnterPhase`
(`src/NoSilence/Detection/DecisionEngine.cs:355`) already counts the transitions.

---

## NS-11 — The full-screen signal asserts `Busy` during ordinary desktop work

**Priority: medium.** Found while smoke-testing the call work on 5 August, not in the original
notes. Probably the second-largest false-positive source after calls.

**Evidence.** A `--diagnose` pass at 17:18, with nothing full-screen running, reported:

```
  process                    endpoint                   dBFS  sustained rule         counts
  chrome.exe                 Headphones (HyperX Cl…    -14.9      11.3s chrome.exe   YES
  Full screen                                                           -            YES
```

Nineteen ducks in today's log name it, every one of them `Busy` rather than `FullScreenD3D` or
`PresentationMode`:

```
2026-08-05 09:39:51 [INF] SILENT — Full screen: Busy application in the foreground
```

**Diagnosis, tentative.** `QUNS_BUSY` is documented as "a full-screen application is running
**or** Presentation Settings are applied", and `DecisionEngine.EvaluateShellSignals` treats it
the same as true exclusive full screen. Something on this machine holds that state during
normal work — `RustDesk.exe` is running and remote-desktop tools are a plausible cause, but
that is a guess and needs confirming.

**The tension worth knowing before touching it.** `Busy` is also what correctly caught the game
in NS-7 — `FullScreenD3D` never fired. So `Busy` cannot simply be dropped from the trigger set
without losing the one case the full-screen signal demonstrably gets right.

**How to investigate.** Log the raw `ShellActivity` value each time it changes, over a normal
day, and correlate with what was in the foreground. If `Busy` proves to be permanently on
because of a background tool, the options are to require it to coincide with a foreground
window that actually covers a monitor, or to narrow the trigger set to
`FullScreenD3D | PresentationMode` and accept that borderless games are already handled by the
per-application audio check.

**Aside, from the same run.** `steam.exe` holds a capture session open on the microphone
permanently. It does not count, because it is judged on level and reads −100 dBFS — a live
demonstration of why the call latch in NS-2 arms on signal rather than on an open microphone.
Leave it out of `MicExclusions`: Steam has voice chat, so level is the right test for it.

## NS-10 — Record a call before tuning anything

**Do this first.** NS-1 and NS-2 are both timing changes, and `NEXT-STEPS.md` step 2 exists for
precisely this reason:

```
NoSilence.Console.exe --diagnose --seconds 600 --jsonl call.jsonl
```

Run it across one real call. Talk, pause, let the other end talk, type, leave the meeting. Then
every proposed value for `CallReleaseMs`, every version of the capture latch, can be replayed
against the same ten minutes in a second, without booking another meeting. Keep the recording
in the repo or alongside it — this is the expensive artefact, not the code.
