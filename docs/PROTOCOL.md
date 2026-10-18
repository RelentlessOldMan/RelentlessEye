# RelentlessMonitor wire protocol (v1)

You only need this if you are writing a client in a language other than C# or Python. The C# and
Python clients already implement all of it.

## Transport

- TCP. The hub listens on port **47800** by default, bound to localhost.
  - `"bind": "all"` in `hub.json` (or `--bind all`) opens it to the network.
- One JSON object per line, UTF-8, `\n`-terminated.
  - The hub drops a client line over 1,048,576 characters and answers
    `{"type":"error","text":"message over 1048576 characters dropped"}`. The connection stays up.
  - Split a big update (a long history replay) into several lines. A `streams` part continues as
    `append`. The C# and Python clients do this themselves.
  - Lines from the hub can be longer (a monitor's full app state), so accept up to 64 MB.
- Property names are camelCase and absent fields are omitted. Unknown fields are ignored.
- JSON has no NaN or infinity. Don't send them: the C# client sends them as the strings `"NaN"`,
  `"Infinity"` and `"-Infinity"`, and the Python client sends `null`.
- Every message may carry `"v": 1`. A message with a higher `v` is rejected.
- Clients connecting from another machine must send the hub's `token` (from
  `%LOCALAPPDATA%\RelentlessMonitor\hub.json`). Loopback clients don't need one.
- Liveness:
  - Send `{"type":"ping"}` after 20 s with nothing else to send.
  - A peer that sends nothing for 60 s is considered dead, in both directions.
- Clients should never block on the hub. Keep the latest values while disconnected, retry, and replay
  on reconnect (see *Reconnect* below).

## Handshake

The client's first line:

```json
{"type":"hello","v":1,"role":"app","app":"CodeCarver","instance":"4711","machine":"DEVBOX","version":"2.3.0",
 "session":"9f2c…","token":"…only for remote…","provides":[{"name":"status","description":"current stage"}],
 "layout":{ …see Layout… }}
```

- `role`: `app` (feeds a panel) or `monitor` (displays panels).
- `app` is required. `machine` is optional; without it the key starts with `?`. `instance` is
  optional: use it when several copies run on one machine.
- `session` is optional: a random id the client picks once and sends with every hello. It lets a
  quick reconnect take its panel back (see below).

The hub answers `{"type":"welcome","key":"DEVBOX/CodeCarver/4711","text":"…"}`. If it refuses, it
answers `{"type":"error","text":"why"}` and closes the connection.

- **Key:** `machine/app[/instance]`. If that key is already connected, `#2`, `#3`… is appended.
- A reconnect with the same identity gets the same key and panel back.
- If the key is still connected (the old connection hasn't timed out yet) but the hello carries the
  same `session`, the hub closes the old connection and the new one takes the key. There's no `#2`.

After the handshake the hub may send `{"type":"error","text":"…"}` without closing, for example for
an unparseable line, an unknown message type, or a line that is too long.

## Layout

The app sends its layout with the hello, or later as `{"type":"layout","layout":{…},"provides":[…]}`.
`provides` is optional; when present, it replaces the list from the hello.

- A new layout drops values for widgets that no longer exist or have become a chart or feed. It also
  drops history for streams that are gone, and trims history to the new limits.
- A layout with no widgets keeps everything, until a real layout arrives.
- Values for ids the layout doesn't have are ignored. With no layout at all, up to 500 ids are kept.
- Null sections and widgets, and widgets without an `id`, are dropped. A widget without a `kind` is
  `text`. `min` and `max` must be finite numbers.

```json
{"title":"Code Carver",
 "sections":[
   {"title":"Job","columns":2,"widgets":[
     {"id":"state","kind":"indicator","label":"State","states":{"idle":"gray","running":"green","failed":"red"}},
     {"id":"progress","kind":"progress","label":"Progress","span":2},
     {"id":"cpu","kind":"chart","label":"CPU","limit":120,"min":0,"max":100,"unit":"%"},
     {"id":"log","kind":"feed","limit":200},
     {"id":"restart","kind":"button","label":"Restart","confirm":"Really?","color":"red"},
     {"id":"paused","kind":"toggle","label":"Paused"}]}]}
```

Widget options: `id`, `kind`, `label`, `span` (grid columns), `color`, `min`, `max`, `unit`,
`limit`, `states`, `confirm`, `options`.

| kind | value it shows |
|---|---|
| `text` | any value, shown as text |
| `kv` | an object, shown as a key/value table |
| `indicator` | a state string (colored via `states`), or `{"state","color","text"}` |
| `progress` | a number between `min` and `max` (default 0–1), or `{"value","text"}`; a value with neither means indeterminate |
| `gauge` | a number between `min` and `max` (default 0–100), with `unit` |
| `chart` | **stream** of numbers, last `limit` kept (default 120) |
| `feed` | **stream** of lines: strings or `{"text","level","ts"}` (`level`: info/warn/error/debug); last `limit` kept (default 200) |
| `button` | optional `{"enabled":false,"label":"…"}`; clicking sends an action |
| `toggle` | `true`/`false`; flipping sends an action, and the app confirms by setting the value |
| `choice` | one of `options`, shown highlighted; picking another sends an action with it, and the app confirms by setting the value |
| `flags` | an array of the `options` that are on; clicking one sends an action with the whole new array, and the app confirms by setting the value |

`options` (choice, flags) is a list. Each entry is a string, or an object
`{"value","label","color","confirm"}` when it needs more:

```json
{"id":"mode","kind":"choice","label":"Mode","states":{"auto":"green","steady":"orange"},
 "options":["auto","steady",{"value":"hold","label":"Hold","color":"red","confirm":"Hold everything?"}]}
{"id":"res","kind":"flags","label":"Resources","options":["cpu","gpu","ram","network","disk"]}
```

An option's color is its own `color`, else its entry in `states`, else the widget's `color`. Its
`confirm` overrides the widget's `confirm`. The monitor never shows a selection the app didn't set.

Monitors draw unknown kinds as text. Colors are names (`green`, `red`, `orange`, `ok`, `warn`,
`error`, …) or hex: `#rgb`, `#argb`, `#rrggbb` or `#aarrggbb`. Anything else falls back to the
default color.

## Updates (app → hub)

```json
{"type":"update","values":{"state":"running","progress":{"value":0.4,"text":"40%"},"cpu":37.5},
 "append":{"log":[{"text":"started","level":"info","ts":"2026-10-16T10:00:00-07:00"}]},
 "streams":{"cpu":[1,2,3]}}
```

The hub applies the parts in this order:

1. `streams` replaces a stream's whole history.
2. `values` replaces plain widgets and appends one item to streams.
3. `append` adds several items to streams.

The hub keeps the latest state of every app (offline ones too, until they are forgotten) and passes
each update on to monitors as `{"type":"update","key":…,"ts":…,"values"/"append"/"streams"}`.

### Reconnect

After a welcome, an app should send one update containing every current value plus `streams` for
each chart and feed. Because `streams` replaces history, a reconnect never duplicates lines.

## Requests and responses (either direction)

```json
{"type":"request","id":"a1","name":"CodeCarver.status","args":{…}}
{"type":"request","id":"a2","name":"status","to":"DEVBOX/CodeCarver/4711"}
{"type":"response","corr":"a1","result":{…}}
{"type":"response","corr":"a2","error":"what went wrong"}
```

- **Target:** `App.name` targets an app by name, or `to` names a key or app.
- **Delivery:** the hub forwards the request with its own `id` and the sender, stamped by the hub
  (a client can't set these on a request it sends). The app answers with `corr` set to that id.
  - `role`: `monitor` (a monitor or `rmon`: a person) or `app`.
  - `key`: the sender's key, e.g. `monitor:3` or `DEVBOX/CodeCarver`.
  - `app` and `machine`: what the sender's hello said, e.g. `RelentlessMonitor`.
  - The C# client shows this to handlers as `MonitorClient.Sender`, and the Python client as
    `mon.sender`. Hubs before 1.1 send only `key`.
  - Any process that can reach the hub can connect as a monitor, so `role` tells a person from an app
    that behaves, not from a hostile process. By default only this machine can reach the hub.
- **Failure:** the requester gets an error response if the app is offline or unknown, disconnects
  before answering, or takes more than 30 s.
- **Actions:** buttons, toggles, choices and flags arrive at the app as the request `$action` with
  `args: {"widget":"restart","value":…}`: null for a button, a bool for a toggle, an option for a
  choice, an array of options for flags. The C# `OnChoice`/`OnFlags` and Python `on_choice`/`on_flags`
  refuse values that aren't options. Reply with any result or an error; the
  monitor shows it next to the control.

Hub built-ins (target `hub`):

| request | result |
|---|---|
| `hub.info` | version, machine, uptime, port, bind, app/monitor counts |
| `hub.apps` | every known app: key, identity, connected, lastSeen, provides |
| `hub.app` `{"key"}` | that app's full state; `key` may also be an app name |
| `hub.forget` `{"key"}` | drops an offline app's panel |
| `hub.shutdown` | stops the hub (loopback only) |

## Monitors

After the welcome, a monitor receives:

- `{"type":"app","state":{…full AppState…}}` for every known app, and again whenever one connects or
  changes its layout.
- `update` messages as above.
- `{"type":"gone","key":…,"removed":false}` when an app goes offline (`removed: true` means it was
  forgotten).

Monitors send `request` messages to act on apps or query the hub.
