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

**Automatic unmuting — done 7 August, verified on hardware 11 August.** The set had been left
muted again overnight (`09:05:30 [WRN] … is muted in Windows`); the next launch logged
`09:39:19.671 Cleared the Windows mute on SAMSUNG (the output device was just opened)` and the
warning did not return. Asked for directly: *"unmute the source if it's muted on startup"*. `Output.MakeAudibleOnOpen`, on by default, reversing the "defaulting to
off" position below — the ask is the authorisation, and a daily fault that the app can already
detect and already knows how to fix is a poor thing to make somebody click.

It fires as the device is opened rather than at launch, which is a superset of what was asked:
the endpoint is reopened after a TV power cycle and after a machine resume, and those are the
moments the mute actually appears. Never while the device is running — muting the output during
playback is a deliberate act and has to stick, which is the whole reason this is safe to do
without asking.

**Still open.**

1. ~~Unmuting automatically when the endpoint is reopened, behind a setting defaulting to off.
   Deliberately not done yet: changing system volume without being asked wants watching first.~~
   Done, and defaulting to on. Watch the log for `Cleared the Windows mute on … (the output
   device was just opened)` and check it never appears when the mute was meant.
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

**Done — 7 August. Verified on hardware 11 August 09:39: 28 seconds from launch to a television
that was off being on, nobody touching anything.** `TvPolicyConfig.WakeAtStartup`, on by default,
and the wake happens rather than being offered. The same run proved the power query was necessary:
the set answered `Standby` while Windows had the endpoint present and the app was already
"playing" into it, which is exactly the condition that made the first version do nothing.

**Priority: low. Needs a decision more than code.**

09:21, *"TV was off, I turned it off, so I turned it back on."* Automatic TV wake and sleep are
off by default (`NEXT-STEPS.md:69`), so this is the configured behaviour, not a fault.

The decision: should playback starting after a machine wake offer to turn the television on —
a balloon with an action rather than doing it silently? Given `TvPolicy` already has a user-veto
concept for the opposite direction, the asymmetry is a bit odd.

**How it was decided.** Asked for directly — *"the app should turn the target audio output on (if
it's a TV for example) … on startup"* — so it acts rather than offering. A balloon would have put
a click between the user and the thing they had just said they wanted, every single morning.

**What was built.** For five minutes after launch the two-minute wants-to-play requirement drops
to fifteen seconds, and it applies independently of `WakeEnabled`. That independence is the
substantive decision: continuous automatic waking is what people leave off, because it can act at
any moment of the day and a power command is not a small thing. A single attempt bounded to the
minutes after launch is predictable, and it is what "turn it on when I sit down" actually means.
The user veto is what keeps NS-8's own scenario — *"I turned it off, so…"* — from being
re-litigated at the next logon: switch the set off by hand and a restart inside the hour changes
nothing.

Fifteen seconds rather than zero, because the output endpoint takes a moment to open, so for the
first seconds after launch a television that is already on still reads as off — and because a
machine that comes up into a call has nothing to play and should turn nothing on.

**The bug this nearly shipped with.** The first version was unreachable on the only machine it
matters on, and today's log is the proof. At 12:36:05 NoSilence started; at 12:36:06 it was
"Playing to SAMSUNG"; at 12:36:25 the user clicked **Turn on** and the set answered *standby*.
The HDMI endpoint was Active for a television that was asleep — the `KEY_POWEROFF` quirk in
`TV.md`, exactly as documented — and `ShouldWake` refuses to act while the endpoint is present.
The feature would have done nothing, every morning, silently.

So the policy now takes an optional `ReportedPower`, and `TvService.AskTheTelevisionOnce` fills
it with one power query at launch, for providers that can answer. Only during the startup window:
the endpoint remains the sensor for the continuous rule, where it is free and conservative, and
polling a set all day to make waking it easier is the wrong trade. `wol` and `shell` without a
state command derive their answer from the endpoint anyway, so for them nothing changes.

Worth remembering as a pattern: the two facts that killed the first attempt were both already
written down in `TV.md`, and neither was noticed until the log was read.

**Fixed on the way past.** `WantsToPlaySince` and `IdleSince` were being persisted with the rest
of `TvPolicyState`, so a timestamp from the previous run could satisfy the two-minute rule on the
very first tick after launch — a power command before anything at all had been observed.
`TvPolicy.BeginSession` now clears both, and `TvService.Configure` calls it.

**Known wrinkle, not addressed.** `IDisplayController.WakeAsync` returns `true` both for "we
turned it on" and for "it was already on", and `TvService` records `WeWokeIt` from that. A set
that is on but showing another input has no HDMI endpoint, so the policy will attempt a wake,
the controller will correctly send nothing — and `WeWokeIt` becomes true for a television nobody
here woke. It only matters if `SleepEnabled` is also on, which is off by default, but the fix is
a three-valued wake result (`Woken` / `AlreadyOn` / `Failed`) across the four controllers.
Pre-existing; the startup wake makes it easier to reach.

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

**How to investigate.** `DetectionService.Capture` now logs the raw `ShellActivity` whenever it
changes, so the next normal day of use produces the evidence without anyone having to watch for
it:

```
[INF] NoSilence.Detection.DetectionService: Shell activity is now Busy (was AcceptsNotifications).
```

Correlate those lines with what was in the foreground. If `Busy` proves to be permanently on
because of a background tool, the options are to require it to coincide with a foreground
window that actually covers a monitor, or to narrow the trigger set to
`FullScreenD3D | PresentationMode` and accept that borderless games are already handled by the
per-application audio check.

**Aside, from the same run.** `steam.exe` holds a capture session open on the microphone
permanently. It does not count, because it is judged on level and reads −100 dBFS — a live
demonstration of why the call latch in NS-2 arms on signal rather than on an open microphone.
Leave it out of `MicExclusions`: Steam has voice chat, so level is the right test for it.

## NS-12 — Waking the PC did not wake the television, two mornings running

**Done — 11 August, unverified.** Reported: *"When I wake the PC from sleep two days in a row the
TV did not turn on. Are the events coming in properly?"* The answer turned out to be no, and for a
more interesting reason than expected.

**What the evidence said.** Five separate reasons, each sufficient on its own:

1. **The running app predated the feature.** Process 33492 started 7 August 12:36:03 from a binary
   dated 4 August, and was still running on 11 August. NS-8's startup wake had never executed once.
2. **A wake is not a launch.** `_startedAt` was set in `TvService.Configure`, at launch only. The
   app runs for weeks; the startup window had closed on 7 August and could not reopen.
3. **`PowerModeChanged` has never fired.** `grep -c "Resumed from sleep" *.log` returns 0 for all
   seven log files. `PlaybackEngine`'s resume path has therefore never run either, which also
   settles the "sleep/resume recovery: not verified" item in `NEXT-STEPS.md` — it is not that it
   was not verified, it is that the event never arrives.
4. **The machine does not suspend.** The System log has no `Kernel-Power` 42/107 pair since
   8 August. What it has is `Kernel-Power 187` — a user-mode process called `SetSuspendState` —
   followed by `Kernel-Power 59 — "The system is entering Away Mode"`, at 9 Aug 21:22:08 and
   10 Aug 20:48:23, matching NoSilence's own endpoint lines to the second. Away Mode keeps running
   with the display off. There is no resume, so no resume notification, and **an ETW listener on
   the kernel power provider would have heard nothing either** — worth knowing, since that was the
   suggested fix.
5. **The endpoint flap vetoed waking anyway.** 10 Aug 09:24:46 endpoint present → 09:24:48
   Unplugged → 09:24:48 *"Television wake paused until 10:24 (you turned it off)"* → 09:24:51
   present again. A two-second flap bought an hour of not waking, every morning, and would have
   defeated the startup wake even if everything above had worked.

**What was built.** `WakeWatch`, which treats a wake as a launch and finds it from three polled
signals rather than one notification: a wall-clock gap between ticks (real suspend), the endpoint
returning after hours (television off overnight), and input after fifteen minutes of silence (Away
Mode, where neither of the others has an edge). `PowerModeChanged` is still wired up for machines
where it works. Ten tests, and the two that matter are drawn from the logs above.

Also: the manual-power-off veto now waits fifteen seconds for the endpoint loss to persist before
believing it, which is the delayed-confirmation shape used in the reporter's own
`PowerEventTraceProvider` example for the same reason.

**Acceptance.** Sleep the machine overnight with the set off; in the morning the television comes
on without being asked, and the log names the signal that noticed. Not yet observed — the fix has
never met a morning.

## NS-13 — A meeting became seven calls, and each one announced itself

**Done — 16 August, unverified against a real meeting.**

**Observed**, 14–15 August: *"I keep getting you are in a zoom call toasts"*; *"Seems that the
toast appears everytime I make a noise, I am alone in the meeting right now so its very
noticeable"*; and, from 12 August, *"I joined zoom and it did not mute had to snooze."*

**Evidence.** `nosilence-20260814.log`, one meeting:

```
10:20:41  SILENT   In a call — Zoom.exe
10:23:09  SILENT   Quiet — resuming in 14 s      ← 2m28s after the last word
10:23:32  PLAYING  Nothing else is playing
10:25:07  SILENT   In a call — Zoom.exe          ← same meeting, new call, new balloon
10:27:13  SILENT   Quiet — resuming in 15 s
10:27:28  PLAYING  Nothing else is playing
10:30:47  SILENT   In a call — Zoom.exe
```

Seven of those before 11:40. The gaps are all the same length because they are all the same
thing: `CallIdleTimeoutMs`, two minutes, expiring.

**Diagnosis.** NS-2 made a call *arm* on sustained microphone signal and *hold* while the
capture session stayed open, and the arming half was the mistake. Level is the wrong axis in
both directions. Joining a meeting and listening never armed anything, which is the 12 August
note — the music played over the meeting until the user spoke, and the answer was Snooze. And
once armed, a two-minute quiet stretch was read as the meeting ending, so `EndCall` ran, the
music came back, and the next word armed a fresh call. The balloon fired on every one of them,
which is what made it visible.

The reasoning behind arming on level was that "the microphone is open" is what
`TreatActiveCaptureAsNoise` does, and that is off by default because OBS and Voicemeeter hold a
session open for ever. True of the *global setting*; not true of a rule that applies only to
`CaptureMode.Call` applications. Those open a capture session on joining a meeting and drop it
on leaving — visible in the same log, where the hold survived 32 unbroken minutes at 11:07 and
ended cleanly when Zoom closed the session.

**What was built.**

- A call arms on the conferencing client's capture session going **Active**, with no level
  requirement. Everything without a call rule is still judged on level exactly as before.
- `CallIdleTimeoutMs` 2 min → **30 min**, and it is now genuinely a safety net rather than the
  everyday mechanism. It logs a line when it fires, so if some client really does hold the
  microphone open after a meeting, the next log says which.
- The timeout **latches** to the session that exhausted it (`DecisionState.ExhaustedCallSessionId`).
  Without that, arming on the open microphone alone would restart the same dead call on the very
  next tick, and the bound would do nothing at all. Real signal, or the session closing, clears it.
- The call balloon moved to `NotificationLevel.All` and gained a ten-minute per-call cooldown.

**Acceptance.** One meeting produces one `In a call` line and one resume. Four new tests,
including `JoiningACallSilencesBeforeAnybodySpeaks` and `AQuietStretchInAMeetingDoesNotEndTheCall`,
the latter drawn from the log above.

---

## NS-14 — The flap warning fired twice an hour, about behaviour that was correct

**Done — 16 August.** Two separate faults behind one complaint.

**Observed.** *"I kept getting toasts the night prior when I was watching TV about music
starting and stopping often. This happened due to me pausing videos or looking for next to play
which is fine."* And the next day: *"throughout the day multiple time I got the toast about many
pauses which again is expected."*

**Evidence.** Eleven warnings on 15 August, arriving in pairs seconds apart:

```
09:56:34.727 [WRN] Play/silence has flipped 20 times in the last hour…
09:56:39.138 [WRN] Play/silence has flipped 20 times in the last hour…
18:37:14.216 [WRN] …
18:37:59.973 [WRN] …
```

**Diagnosis.**

1. `DetectionService.Tick` tested `_state.TransitionsThisHour == 20` inside the block that runs
   whenever a decision is logged. Ducked→Releasing is a second logged change at the same count,
   so every warning fired twice. NS-9's *"the detection service raises this at most once an hour,
   so it needs no rate limiting of its own"* was therefore wrong, and `NotifyFlapping` passes
   `force: true`, so both balloons got through.
2. More importantly, the warning was telling the user something they already knew. Pausing a
   video and picking the next one *is* twenty transitions in an hour, and the app was working.
   NS-9 surfaced it at every notification level; the default is `ErrorsOnly`, and this is not an
   error.

**What was built.** `DecisionState.ShouldReportFlapping` — a latch, re-armed when the transition
window rolls over, and on the state rather than at the call site so it can be tested through the
real engine (`TheFlapWarningIsRaisedOncePerHour`). The balloon now requires
`NotificationLevel.All`. The log warning is unchanged at every level, which is where it was
always actually useful.

---

## NS-15 — The startup wake waited for silence that never came

**Done — 16 August, unverified against a real morning.** Two independent faults, either of which
was enough on its own.

**Observed.** *"TV did not turn on when I turned on NoSilence.exe"*, and *"13:20 PC finally
turned on when I went back from my lunch break, but it did not turn on when I opened the app or
when I turned PC on? Seems to not be very durable."*

**Evidence, fault one.** `nosilence-20260814.log`:

```
09:45:29.960  NoSilence started.
09:45:30.377  The television reports "Standby" at startup.
09:45:32.555  SILENT — chrome.exe: peaked at -4.3 dBFS over 2.5 s
10:20:31.709  PLAYING — Nothing else is playing
13:19:52.640  The machine is back (input arrived after a long silence)
13:20:13.633  Waking the television (after a wake).
```

Everything NS-8 needed was in place at 09:45:30 — the set answered `Standby`, there was music,
nothing was blocked. The startup wake still could not fire, because it needed fifteen seconds of
`WantsToPlaySince` running continuously and Chrome made a sound three seconds in. The timer reset
and stayed reset until 10:20, thirty-five minutes after the five-minute window had closed. The
television came on at 13:20 because coming back from lunch counted as another wake, which is the
"finally" in the note.

**Evidence, fault two.** Two mornings running, on the same tick:

```
2026-08-16 10:39:41.676  The machine is back (the clock jumped, so the machine was suspended)
2026-08-16 10:39:41.677  Television wake paused until 11:39 (you turned it off).
```

Nobody had touched the remote. `ConfirmOrForgetAManualPowerOff` believes an endpoint loss once it
has persisted for fifteen seconds — and a suspended machine satisfies that for free, because no
ticks run while it is away, so the first tick after a resume finds a loss that is hours old. The
veto NS-12 was careful to delay then blocked waking for the following hour, every morning. Note
that the fifteen-second confirmation added in NS-12 did not cause this and does not prevent it;
the flaw is in measuring elapsed time across an interval in which the process was not running.

**What was built.**

- Inside the startup window the wake is timed from the launch or the wake itself, not from
  `WantsToPlaySince`. The guards that matter are untouched: the set's own report of being off,
  a library with tracks in it, not snoozed, not always-silent, and the manual power-off veto.
  Something else playing is a poor reason to leave the screen dark — this television *is* the
  output device, so if it is off nobody can hear that either.
- `TvService` drops a pending, unconfirmed endpoint loss whenever a wake is observed. Only the
  pending one: a veto already confirmed, with somebody sitting at the machine, really was an
  instruction and stays.

**Acceptance.** Sleep the machine overnight with the set off; in the morning the television is
on within about twenty seconds, and no `wake paused` line appears. Not yet met a morning.

---

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
