# Turning the television on

Optional, and off by default. Nothing touches your television unless you configure it and tick
the boxes.

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

## When it decides to act

Waking requires **all** of: it is enabled, the television is believed off, the library is not
empty, you are not snoozed or set to always-silent, the engine has wanted to play continuously
for two minutes, five minutes have passed since the last power command, and fewer than six power
commands have been sent in the last hour.

The two-minute requirement exists so a brief gap between videos can never power-cycle a
television. The hourly cap is a hard circuit breaker.

**If you switch the television off by hand, NoSilence stops trying for an hour.** It detects
this by noticing the audio endpoint disappear when it did not ask for that. Without the rule the
two of you end up fighting over the television. Asking it to wake explicitly clears the veto.

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
