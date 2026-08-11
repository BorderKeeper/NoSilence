# Turning the television on

Optional, and off by default: with no provider configured nothing here runs at all, and nothing
touches your television until you have pointed NoSilence at one.

Once you have, there is one thing it does without being asked further — **it turns the set on
shortly after NoSilence starts**, if there is music to play and the set is off. See
[When it decides to act](#when-it-decides-to-act); the checkbox is *Turn the television on when
NoSilence starts*. Everything else waits for a box to be ticked.

## Why this is harder than it should be

You would expect HDMI-CEC — the protocol a games console uses to switch your TV on and select
its input. A PC cannot use it. Consumer NVIDIA cards do not expose CEC at all, and neither
Windows nor the GPU drivers offer an API for it. Real CEC from a PC needs a USB adapter such as
a Pulse-Eight, at which point you may as well use the shell-command provider below.

So NoSilence reaches the television over the network instead.

## Setting it up

```
NoSilence.Console.exe --discover-tv
```

That sweeps your local subnets and reports any Samsung sets, with both the MAC the television
claims and the one ARP actually sees:

```
TV From Hell (QE75Q7FAAUXXH) at 192.168.1.238
    power        : standby
    mac (its own): 4c:57:39:b8:05:5d
    mac (ARP)    : 4C:57:39:B8:05:5D   <- prefer this one
```

Then **Settings → Television**, or use **Find my television…** there, which does the same thing
and fills the fields in. Press **Pair** and accept the prompt that appears on the television.
The token is saved, so it only ever asks once.

## Prefer the ARP address

A Samsung set reports its MAC as `wifiMac`, and on a wired set that is frequently the Wi-Fi
radio's address — a magic packet sent there will never wake the wired interface. ARP reports
the adapter actually carrying that IP, so it is preferred automatically. You can override it by
hand if waking fails.

## Wake-on-LAN, and when it does not work

The magic packet goes to the broadcast address, every directed subnet broadcast and the unicast
address, on ports 9 and 7, from **every local interface in turn**. That is deliberate: with a
VPN, Hyper-V vSwitch or WSL adapter present — and one development machine here has three
subnets — an unbound broadcast routinely leaves through the wrong adapter and never arrives. It
is the most common reason Wake-on-LAN "does not work".

For a Samsung set it also needs **Network Standby** enabled:
*Settings → General → Network → Expert Settings → Power On with Mobile*.

Even then, some televisions simply ignore magic packets. One 2017 QLED tested here does: 54
packets from three interfaces, no effect, while the set answered HTTP throughout.

## The fallback that usually works

The documented behaviour is that a Samsung set closes its remote-control port in standby, which
would make the remote protocol useless for switching it on. That is not universally true — the
set tested here keeps ports 8001, 8002 and 8080 open and answers its device-info API while
asleep.

So NoSilence sends Wake-on-LAN first, waits eight seconds, and then falls back to `KEY_POWER`
over the remote channel. On that television the fallback is what works, in about two seconds.

`KEY_POWER` is a **toggle**, so it is only ever sent once the set has explicitly reported
standby. If it reports being on while the PC's HDMI audio endpoint is missing — meaning it is
showing another input — nothing is sent at all, rather than risk switching it off.

## Do not trust the HDMI endpoint alone

Windows removes the HDMI audio endpoint when a television powers off, which makes endpoint
presence a free and instant "is it on?" sensor. That holds when the set is switched off with its
remote. It does **not** hold for a standby entered via `KEY_POWEROFF`, which can leave the HDMI
link asserted:

```
HDMI audio endpoint present: True     (Windows)
PowerState: standby                   (the television)
```

The television's own report therefore wins, and the endpoint is consulted only when the set
reports nothing useful or cannot be reached.

This is also why the startup wake asks the set directly. The policy's normal power sensor is the
endpoint — free, instant, and consulted four times a second — and on this hardware it says
"present" for a television that is fast asleep, which would leave the one wake worth sending
permanently blocked. So at launch NoSilence makes a single request for the real answer:

```
[INF] NoSilence.Tv.TvService: The television reports standby at startup.
```

Once per run, only while starting up, and only for a provider that can answer. If the request
fails the endpoint is used as before. Polling the television all day would be the wrong trade;
asking it once, at the only moment it changes a decision, is not.

## When it decides to act

Waking requires **all** of: it is enabled, the television is believed off, the library is not
empty, you are not snoozed or set to always-silent, the engine has wanted to play continuously
for two minutes, five minutes have passed since the last power command, and fewer than six power
commands have been sent in the last hour.

The two-minute requirement exists so a brief gap between videos can never power-cycle a
television. The hourly cap is a hard circuit breaker.

### Starting up — and waking up — are the exception

For the first five minutes after NoSilence launches **or after the machine comes back**, the
two-minute requirement drops to fifteen seconds, and it applies whether or not automatic waking is
switched on. Sitting down at a machine that has just come up is not the "brief gap between videos"
the long wait exists to survive, so measuring one with the other only makes you wait for something
obvious.

### Noticing that the machine came back

Windows has a resume notification, `PowerModeChanged`. It is subscribed to, and on the machine
this was written on it has **never once fired** — seven days of logs, several nights, not one
"Resumed from sleep". The System event log says why: there are no `Kernel-Power` 42/107
suspend/resume pairs at all, only

```
Kernel-Power 59 — The system is entering Away Mode.
```

In Away Mode the machine does not suspend. It keeps running with the display and audio switched
off, so there is no resume to be notified about — and an ETW listener watching the kernel power
provider for suspend/resume events would hear exactly as much as we did, which is nothing.

So three signals are watched instead, any of which counts as a wake, all of them polled from
state the tick already has:

| Signal | Catches | Threshold |
|---|---|---|
| The wall clock jumped between ticks | A real S3 suspend or hibernate — the process was frozen | 90 s |
| The output endpoint returned | The television, and with it the HDMI endpoint, had been gone for hours | 5 min away |
| Input arrived after a long silence | Away Mode, where neither of the above has an edge to offer | 15 min idle |

Plus `PowerModeChanged` itself, which costs nothing to keep for the machines where it does work.
The log names whichever one fired:

```
[INF] NoSilence.Tv.TvService: The machine is back (input arrived after a long silence);
      the television gets another look.
```

The third one is the loose one: it fires when somebody returns to the desk, whether or not the
machine slept. That is deliberate — on hardware that enters Away Mode it is the only edge there
is — and the consequences are bounded by everything else in this section: the set is asked whether
it is already on, and told to turn on only if it is off and there is music waiting.

Every other guard still holds, and the important one is the veto: **if you turned the television
off by hand less than an hour ago, restarting NoSilence — or logging on again — will not turn it
back on.** That state is persisted for exactly this reason. Nor will it act if the machine came
up into a call or a game, because then nothing wants to play.

The window is five minutes, the same as the cooldown between power commands, so this is one
attempt rather than a series of them. Look for `Waking the television (at startup)` in the log;
the ordinary rule says `(automatically)` and the tray's own button says `(on request)`.

Turn it off with *Turn the television on when NoSilence starts* if you would rather decide for
yourself each time — the Television submenu's **Turn on** is always there.

**If you switch the television off by hand, NoSilence stops trying for an hour.** It detects
this by noticing the audio endpoint disappear when it did not ask for that. Without the rule the
two of you end up fighting over the television. Asking it to wake explicitly clears the veto.

The loss has to **last fifteen seconds** before it counts. The endpoint flaps — it goes Unplugged
and comes back within a second or two whenever the set changes state, three times in one recorded
morning — and every one of those flaps used to be filed as "you turned it off", which then
suppressed waking for the next hour. A pause before believing it fixes that.

Turning the set off is off by default, and when enabled it will only ever power down a
television it turned on itself.

The tray's Television submenu has a one-click **Turn off all television control**.

## Other televisions

| Provider | Use it for |
|---|---|
| `wol` | Anything that honours Wake-on-LAN. Wake only. |
| `shell` | A command or a URL — `webostv-cli` for LG, a Home Assistant webhook, a smart plug, `cec-client` if you do own a CEC adapter. |

For `shell`, set a wake command, a sleep command, and optionally a state command whose output is
`on`, `off` or `standby`. Anything starting with `http` is fetched rather than executed.

## Testing without the tray

```
NoSilence.Console.exe --wake-tv
NoSilence.Console.exe --sleep-tv
```

Both print what they tried and what the television reported.
