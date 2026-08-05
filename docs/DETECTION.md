# How NoSilence decides to go quiet

## The short version

Every 250 ms it reads the level of every Windows audio session, per process. A session counts
as noise when it has been above the threshold for most of a trailing window. If anything
counts, the music fades out; when nothing has counted for the release period, it fades back in.

Its own audio is excluded by process ID.

## The numbers

| Setting | Default | What it does |
|---|---|---|
| Threshold | −50 dBFS | Below this is treated as silence |
| Wait before going quiet | 2000 ms | How long a source must keep making sound |
| Wait before resuming | 5000 ms | How long everything must stay quiet |
| Wait before resuming after a call | 15000 ms | Calls get their own, longer wait — see below |
| Fade out / in | 400 ms / 3000 ms | Fast out, gentle back in |
| Poll interval | 250 ms | |

**Why −50 dBFS.** Higher, around −35, misses quiet dialogue and a game with its master at
10% — the failure people notice most. Lower, around −70, starts picking up browser tabs that
hold an idle-but-open audio context and conferencing apps keeping a silent stream alive.

For scale, the previous version of this app compared the raw peak against `0.0001f`. That
looks like a cautious small number and is actually **−80 dBFS**, on a meter that summed every
session at once, so more or less anything pinned it permanently.

**Why a sustain requirement.** Speech and music are bursty even measured as peaks over 250 ms,
so a single-sample test flaps constantly. A source has to be above the threshold for at least
70% of the trailing window. The window length *is* the sustain time.

Two seconds specifically, because a Windows notification chime is about one second. It started
at 1200 ms and a console bell went straight through it. The costs are asymmetric: ducking 0.8 s
later than strictly necessary is barely perceptible, while a false duck buys several seconds of
silence over a chime.

**Why the release is longer than you would think.** It began at 20 seconds so an ad break or
pausing a video to read something would not bring music up over the top. In daily use that was
far too much dead air, so it is 5 now. If a mid-video pause starts talking over you, raise it.

## Per-application rules

Whether a sound is "noise" is a property of the *application*, not of the sound. A Discord
notification and a Discord call have the same level; only their duration differs.

| Mode | Meaning |
|---|---|
| Ignore | Never counts, however loud |
| Tolerant | Counts only after a longer sustain (4 s by default) |
| Trigger | Normal |
| Always trigger | Counts the instant it is above the threshold |

Shipped defaults: system sounds, Explorer, the shell surfaces, Outlook and **all console hosts**
are ignored; Zoom, Discord, Teams, Slack, Telegram and WhatsApp are tolerant; browsers get
2.5 s; Spotify, VLC, MPC and PotPlayer always trigger.

Those same conferencing applications also carry a second, separate setting covering what their
*microphone* use means — see "Calls are a context" below. The two are independent: the mode
above decides how their output is judged, and it is still just audio.

First match wins. Right-clicking a row in **What's playing** inserts a rule at the front, so a
choice you just made outranks the built-in rule it replaces.

## Extra signals

| Signal | Default | Notes |
|---|---|---|
| Microphone in use | on | Catches calls even when the other end is quiet |
| Full-screen application | on | See the caveat below |
| Focus Assist | off | "Do not disturb" does not mean "no music" for everyone |
| Machine idle | off | Cannot see gamepad input or someone watching a film |
| Workstation locked | off | Unambiguous, unlike idle |

## Calls are a context, not a sound

This is the one place the level meter is the wrong instrument, and two days of daily use made
it obvious: **352 play/silence flips in a single day**, very nearly all of them one Zoom call.

The reason is that a call has no continuous sound in it. You stop talking to listen; the other
end pauses; the meter drops below the threshold; five seconds later the music comes back over
the top of whoever started speaking again. At 11:04 on the first day the music returned for
**751 milliseconds** in the middle of a meeting.

So an application marked as one that makes calls — Zoom, Teams, Slack, Discord, Telegram,
WhatsApp — is treated differently on the microphone:

| | |
|---|---|
| **Arms** | Sustained microphone signal, exactly the condition that used to duck directly |
| **Holds** | For as long as that application keeps the capture session open |
| **Ends** | When the capture session closes, or after 2 minutes with nothing from either direction |
| **Then** | 15 seconds of quiet before the music returns, not 5 |

Note what the arming condition is *not*. It is not "the microphone is open" — that is what
`TreatActiveCaptureAsNoise` does, and the reason it is off by default is that OBS, Voicemeeter,
NVIDIA Broadcast and half the headset utilities hold a capture session open permanently. An
idle microphone carries no sustained signal, so it never starts a call. Nothing new begins
ducking here; only the stopping changed.

The two-minute idle timeout is the safety net, and it exists because "hold while the microphone
is open" would otherwise mean "hold until the application exits" for any client that keeps the
device open after a meeting ends. A muted listener is still covered, because the other end
talking is audio from the same application and that keeps the call alive.

While a call is held the tray says **In a call — Zoom** rather than quoting a level, because
"peaked at −18.2 dBFS" is a true statement about a sample and a useless one about why the music
stopped.

## Extra signals, continued

**The full-screen caveat is real.** It uses `SHQueryUserNotificationState`, which is the same
signal Windows uses to suppress its own toasts. That only reports true exclusive full screen
and presentation mode — **borderless-windowed games look like ordinary windows**, and that is
most modern games. Treat it as a supplement; the per-application audio check does the work.

The microphone signal is the likeliest source of false positives, because OBS, Voicemeeter,
NVIDIA Broadcast and several headset utilities hold a capture session open permanently. Those
are excluded by name out of the box, and the list is certainly incomplete. Anything the
per-application rules already ignore is ignored on the microphone too.

## Tuning it against your own machine

No unit test can tell you whether −50 dBFS is right for you. So record a real session and
replay it.

```
NoSilence.Console.exe --diagnose --seconds 300 --jsonl session.jsonl
```

While that runs, do the things that matter: play a video and stop it, pause one mid-play, take
a call, start a game, let a notification arrive. One snapshot per tick is written to the file.
An existing recording is moved aside rather than overwritten — they are expensive to produce.

```
NoSilence.Console.exe --replay session.jsonl
```

That re-runs the recording through the *current* settings and prints every point the decision
would flip, plus how much of the recording was silent and whether it flapped. Change a
threshold, replay, compare — in a second, against real data, without relaunching the game.

```
          at  state    reason
  00:00:00  PLAYING  Nothing else is playing
  00:00:11  SILENT   chrome.exe: peaked at -18.5 dBFS over 2.5 s
  00:00:33  PLAYING  Nothing else is playing

Silent for 00:00:22 (9% of the recording), 3 transitions.
```

This works because the decision engine is a pure function of the snapshot, the settings and its
own state — it takes its time from the snapshot rather than from a clock, so a recording replays
identically.

Add `--play` to hear the decisions while you watch them.

## When it flaps

Three or more transitions inside 30 seconds is flapping, and both `--replay` and the running
app will say so — the app logs a warning once it has flipped twenty times in an hour, so you
find out without having to watch for it.

Usually it means the threshold is too low for something on your machine, or an application
needs a rule. Open **What's playing** and look for the row that keeps turning bold.
