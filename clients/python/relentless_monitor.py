"""
relentless_monitor.py - feed a RelentlessMonitor hub from any Python app. Standard library only;
copy this one file into your project.

    import relentless_monitor as rm

    mon = rm.MonitorClient("CodeCarver", rm.layout(
        rm.section("Run",
            rm.indicator("state", "State", states={"idle": "gray", "running": "green", "failed": "red"}),
            rm.progress("progress", "Progress"),
            rm.feed("log", "Log", span=2),
            rm.button("cancel", "Cancel", confirm="Cancel the run?")),
        title="CodeCarver"))
    mon.on_action("cancel", lambda value: cancel_run())
    mon.start()

    mon.set("state", "running")
    mon.set("progress", 0.4)
    mon.log("log", "parsing 120 files")      # feed: appends a line

Nothing here blocks or raises when no hub is running: values are kept and handed over (with
chart/feed history) when one appears. The hub is found at RELENTLESSMONITOR_HUB (host[:port]) or
localhost:47800; a hub on another machine also needs RELENTLESSMONITOR_TOKEN.
"""

import json
import math
import os
import socket
import threading
import time
import uuid
from datetime import datetime, timezone

__all__ = [
    "MonitorClient", "Response",
    "layout", "section", "text", "kv", "indicator", "progress", "gauge", "chart", "feed", "button", "toggle",
]

PROTOCOL_VERSION = 1
DEFAULT_PORT = 47800
ACTION_REQUEST = "$action"
PING_INTERVAL = 20.0
DEAD_AFTER = 60.0
REQUEST_TIMEOUT = 35.0
MAX_LINE = 1024 * 1024            # longest line the hub accepts from a client
MAX_HUB_LINE = 64 * 1024 * 1024   # longest line we accept from the hub
STREAM_KINDS = ("chart", "feed")
DEFAULT_LIMITS = {"chart": 120, "feed": 200}
MAX_STREAM_LIMIT = 5000


# ---------------------------------------------------------------- layout helpers

def layout(*sections, title=None):
    """The whole panel: sections of widgets."""
    d = {"sections": list(sections)}
    if title:
        d["title"] = title
    return d


def section(title, *widgets, columns=2):
    """A titled group of widgets laid out in a grid of `columns`."""
    return {"title": title, "columns": columns, "widgets": list(widgets)}


def _widget(kind, id, label=None, **opts):
    w = {"id": id, "kind": kind}
    if label is not None:
        w["label"] = label
    w.update({k: v for k, v in opts.items() if v is not None})
    return w


def text(id, label=None, span=None):
    """Value: any text."""
    return _widget("text", id, label, span=span)


def kv(id, label=None, span=None):
    """Value: a dict of label -> value, shown as rows."""
    return _widget("kv", id, label, span=span)


def indicator(id, label=None, states=None):
    """Value: a state string, colored by `states` ({"ok": "green", "failed": "#c00000"})."""
    return _widget("indicator", id, label, states=states)


def progress(id, label=None, max=1.0, span=None):
    """Value: a number 0..max, or {"value": .., "text": ..}."""
    return _widget("progress", id, label, min=0, max=max, span=span)


def gauge(id, label=None, min=0, max=100, unit=None, color=None):
    """Value: a number between min and max."""
    return _widget("gauge", id, label, min=min, max=max, unit=unit, color=color)


def chart(id, label=None, limit=120, min=None, max=None, unit=None, color=None, span=None):
    """Value: a number; each set() appends a point. The last `limit` points are kept."""
    return _widget("chart", id, label, limit=limit, min=min, max=max, unit=unit, color=color, span=span)


def feed(id, label=None, limit=200, span=None):
    """Value: a line of text (or {"text":..,"level":"info|warn|error"}); each set() appends."""
    return _widget("feed", id, label, limit=limit, span=span)


def button(id, label, confirm=None, color=None):
    """Clicking it in the monitor calls the on_action handler for `id`."""
    return _widget("button", id, label, confirm=confirm, color=color)


def toggle(id, label):
    """Value: bool. Flipping it calls the on_action handler with the requested bool."""
    return _widget("toggle", id, label)


# ---------------------------------------------------------------- client

class Response:
    """Answer to request(). Never raised: failures are ok=False with error set."""

    __slots__ = ("ok", "result", "error")

    def __init__(self, ok, result=None, error=None):
        self.ok, self.result, self.error = ok, result, error

    def __repr__(self):
        return "Response(ok=%r, result=%r, error=%r)" % (self.ok, self.result, self.error)


def _resolve_hub(address):
    address = (address or os.environ.get("RELENTLESSMONITOR_HUB") or "").strip()
    if not address:
        return "127.0.0.1", DEFAULT_PORT
    if address.startswith("[") and "]:" in address:
        host, _, port = address[1:].partition("]:")
        if port.isdigit() and 0 < int(port) <= 65535:
            return host, int(port)
    if address.count(":") == 1:
        host, _, port = address.partition(":")
        if port.isdigit() and 0 < int(port) <= 65535:
            return ("127.0.0.1" if host.lower() == "localhost" else host), int(port)
    return ("127.0.0.1" if address.lower() == "localhost" else address), DEFAULT_PORT


class MonitorClient:
    """
    An app's link to the monitor. Describe the panel once, then set() values whenever they change.
    Runs one background thread; every method returns immediately (request() waits for its answer).
    """

    def __init__(self, app, layout=None, hub=None, token=None, instance=None, machine=None, version=None,
                 connect_timeout=0.5, retry_interval=5.0):
        if not app:
            raise ValueError("app name required")
        self.app = app
        self.instance = instance
        self.machine = machine or socket.gethostname()
        self.version = version
        self.key = None
        self.last_rejection = None
        self._host, self._port = _resolve_hub(hub)
        self._token = token or os.environ.get("RELENTLESSMONITOR_TOKEN")
        self._connect_timeout = connect_timeout
        self._retry = retry_interval

        self._lock = threading.RLock()
        self._wake = threading.Event()
        self._stop = threading.Event()
        self._layout = self._plain(layout) if isinstance(layout, dict) else {"sections": []}
        self._widgets = self._index(self._layout)
        self._hello_layout = None   # the layout the last hello carried; replay resends a newer one
        self._session_id = uuid.uuid4().hex  # same across reconnects: lets the hub hand our panel back
        self._values = {}           # widget id -> latest value (non-stream)
        self._streams = {}          # widget id -> list of items (chart/feed history)
        self._p_values, self._p_append, self._p_streams = {}, {}, {}
        self._layout_dirty = False
        self._live = False          # True from the replay snapshot until the connection drops
        self._out = []              # other messages to write (responses, requests)
        self._actions = {}
        self._provides = {}         # name -> (handler, description)
        self._pending = {}          # request id -> [Event, Response]
        self._sock = None
        self._thread = None
        self._connected = False
        self._idle = threading.Event()
        self._idle.set()

    # ---- public API ----

    @property
    def connected(self):
        return self._connected

    def start(self):
        """Starts looking for the hub in the background. Returns immediately."""
        if self._thread is None:
            self._thread = threading.Thread(target=self._run, name="relentless-monitor", daemon=True)
            self._thread.start()
        return self

    def wait_connected(self, timeout):
        """Optional: waits up to `timeout` seconds for the hub. Returns whether connected."""
        end = time.monotonic() + timeout
        while not self._connected and time.monotonic() < end:
            time.sleep(0.02)
        return self._connected

    def set(self, id, value):
        """Sets a widget's value. On a chart or feed this appends a point / line. Never raises."""
        if not isinstance(id, str):
            return
        value = self._plain(value)
        with self._lock:
            self._set_locked(id, value)
        self._wake.set()

    def set_many(self, values):
        """Sets several widgets ({id: value}) in one update. Never raises."""
        try:
            items = list(values.items())
        except Exception:
            return
        items = [(k, self._plain(v)) for k, v in items if isinstance(k, str)]
        with self._lock:
            for k, v in items:
                self._set_locked(k, v)
        self._wake.set()

    append = set

    def log(self, id, text, level="info"):
        """Appends a feed line with a level (info, warn, error); the monitor colors it."""
        self.set(id, {"text": text if isinstance(text, str) else str(text), "level": level,
                      "ts": datetime.now(timezone.utc).astimezone().isoformat()})

    def clear(self, id):
        """Empties a chart/feed, or blanks a plain widget."""
        with self._lock:
            if self._is_stream(id):
                self._streams[id] = []
                if self._live:
                    self._p_append.pop(id, None)
                    self._p_streams[id] = []
            else:
                self._set_locked(id, None)
        self._wake.set()

    def get(self, id):
        """The latest value set for a widget (for a chart/feed: its last item)."""
        with self._lock:
            if id in self._values:
                return self._values[id]
            s = self._streams.get(id)
            return s[-1] if s else None

    def set_layout(self, new_layout):
        """Replaces the panel layout; monitors redraw. Values of widgets that remain are kept."""
        new_layout = self._plain(new_layout) if isinstance(new_layout, dict) else {"sections": []}
        with self._lock:
            self._layout = new_layout
            self._widgets = self._index(new_layout)
            if self._widgets:  # same rules as the hub: an empty layout keeps everything until a real one
                for id in list(self._streams):
                    if not self._is_stream(id):
                        del self._streams[id]
                    else:
                        self._trim(self._streams[id], self._limit(id))
                for id in list(self._values):
                    if id not in self._widgets or self._is_stream(id):
                        del self._values[id]
            if self._live:
                self._layout_dirty = True
        self._wake.set()

    def on_action(self, widget_id, handler):
        """
        handler(value) runs when someone clicks button `widget_id` (or flips that toggle; value is
        the requested bool). Its return value goes back to the monitor; an exception is the error.
        """
        self._actions[widget_id] = handler

    def on_toggle(self, widget_id, handler):
        """handler(bool) runs with the requested state, then the toggle shows that state."""
        def run(value):
            on = value is True
            handler(on)
            self.set(widget_id, on)
            return on
        self._actions[widget_id] = run

    def provide(self, name, handler, description=None):
        """handler(args) answers requests named `name` (callers may write App.name). Declare before start()."""
        self._provides[name] = (handler, description)

    def request(self, name, args=None, to=None, timeout=REQUEST_TIMEOUT):
        """Asks the hub (hub.apps), or another app (OtherApp.thing). Fails fast with no hub."""
        if not self._connected:
            return Response(False, error="not connected to hub")
        rid = uuid.uuid4().hex
        slot = [threading.Event(), None]
        msg = {"type": "request", "id": rid, "name": name}
        if to:
            msg["to"] = to
        if args is not None:
            msg["args"] = self._plain(args)
        with self._lock:
            self._pending[rid] = slot
            self._out.append(msg)
        self._wake.set()
        if not slot[0].wait(timeout):
            with self._lock:
                self._pending.pop(rid, None)
            return Response(False, error="no response within %gs" % timeout)
        return slot[1]

    def flush(self, timeout=1.0):
        """Waits until everything set so far has been written. Returns False on timeout or no hub."""
        end = time.monotonic() + timeout
        self._wake.set()
        while self._connected and time.monotonic() < end:
            with self._lock:
                empty = not (self._p_values or self._p_append or self._p_streams or self._layout_dirty or self._out)
            if empty and self._idle.is_set():
                return True
            time.sleep(0.01)
        return False

    def close(self, flush_timeout=1.0):
        """Sends what's left (briefly) and disconnects."""
        if self._connected:
            self.flush(flush_timeout)
        self._stop.set()
        self._wake.set()
        sock = self._sock
        if sock:
            try:
                sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
        if self._thread:
            self._thread.join(2)

    def __enter__(self):
        return self.start()

    def __exit__(self, *exc):
        self.close()

    # ---- state ----

    @staticmethod
    def _index(lay):
        out = {}
        sections = lay.get("sections") if isinstance(lay, dict) else None
        for s in sections if isinstance(sections, list) else []:
            widgets = s.get("widgets") if isinstance(s, dict) else None
            for w in widgets if isinstance(widgets, list) else []:
                if isinstance(w, dict) and isinstance(w.get("id"), str) and w["id"]:
                    out[w["id"]] = w
        return out

    @staticmethod
    def _plain(value):
        """
        A JSON-safe copy, taken now so later changes to the caller's object don't leak in. Never raises:
        NaN and infinities become None, unknown objects their str(), and anything that still won't
        encode (a cycle, odd keys) becomes str(value).
        """
        try:
            return _finite(json.loads(json.dumps(value, default=str)))
        except Exception:
            try:
                return str(value)
            except Exception:
                return None

    def _is_stream(self, id):
        w = self._widgets.get(id)
        return bool(w) and w.get("kind") in STREAM_KINDS

    def _limit(self, id):
        w = self._widgets.get(id) or {}
        return max(1, min(w.get("limit") or DEFAULT_LIMITS.get(w.get("kind"), 200), MAX_STREAM_LIMIT))

    @staticmethod
    def _trim(items, limit):
        if len(items) > limit:
            del items[:len(items) - limit]

    def _set_locked(self, id, value):
        if self._widgets and id not in self._widgets:
            return  # not in the layout: nothing would show it (the hub ignores it too)
        if self._is_stream(id):
            hist = self._streams.setdefault(id, [])
            hist.append(value)
            self._trim(hist, self._limit(id))
            if self._live:
                pend = self._p_append.setdefault(id, [])
                pend.append(value)
                self._trim(pend, self._limit(id))
        else:
            self._values[id] = value
            if self._live:
                self._p_values[id] = value

    def _hello(self):
        caps = [{"name": n, "description": d} if d else {"name": n} for n, (_, d) in sorted(self._provides.items())]
        with self._lock:
            self._hello_layout = self._layout  # set_layout swaps in a new dict, so identity tells if it changed
        m = {"v": PROTOCOL_VERSION, "type": "hello", "role": "app", "app": self.app, "machine": self.machine,
             "session": self._session_id, "layout": self._hello_layout, "provides": caps}
        for k, v in (("instance", self.instance), ("version", self.version), ("token", self._token)):
            if v:
                m[k] = v
        return m

    def _replay(self):
        with self._lock:
            self._live = True
            self._p_values, self._p_append, self._p_streams = {}, {}, {}
            self._out = []
            batch = []
            if self._layout is not self._hello_layout:  # set_layout between hello and welcome
                batch.append({"type": "layout", "layout": self._layout})
            self._layout_dirty = False
            batch.append({"type": "update", "values": dict(self._values),
                          "streams": {k: list(v) for k, v in self._streams.items()}})
            return batch

    def _pull(self):
        batch = []
        with self._lock:
            if self._layout_dirty:
                batch.append({"type": "layout", "layout": self._layout})
                self._layout_dirty = False
            if self._p_values or self._p_append or self._p_streams:
                u = {"type": "update"}
                if self._p_values:
                    u["values"] = self._p_values
                if self._p_append:
                    u["append"] = self._p_append
                if self._p_streams:
                    u["streams"] = self._p_streams
                batch.append(u)
                self._p_values, self._p_append, self._p_streams = {}, {}, {}
            batch.extend(self._out)
            self._out = []
        return batch

    # ---- connection ----

    def _run(self):
        while not self._stop.is_set():
            wait = self._retry
            try:
                wait = self._attempt()
            except Exception:  # whatever went wrong, keep looking for the hub
                pass
            self._stop.wait(max(0.1, wait))

    def _attempt(self):
        """One connect attempt and, if it works, the whole connection. Returns the wait before the next."""
        try:
            sock = socket.create_connection((self._host, self._port), timeout=self._connect_timeout)
        except (OSError, ValueError, OverflowError):
            return self._retry
        self._sock = sock
        rejected = None
        try:
            rejected = self._session(sock)
        except Exception:
            rejected = None
        finally:
            self._set_connected(False)
            self._sock = None
            # shutdown first: the reader thread's file object holds the socket open past close()
            for close in (lambda: sock.shutdown(socket.SHUT_RDWR), sock.close):
                try:
                    close()
                except OSError:
                    pass
        if rejected:
            self.last_rejection = rejected
            return max(30.0, self._retry)
        return 0.5

    def _send(self, sock, messages):
        data = "".join(line + "\n" for m in messages for line in _lines(m))
        sock.sendall(data.encode("utf-8"))

    def _session(self, sock):
        sock.settimeout(5.0)
        try:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        except OSError:
            pass
        reader = sock.makefile("r", encoding="utf-8", newline="\n")
        self._send(sock, [self._hello()])
        first = json.loads(reader.readline(MAX_HUB_LINE) or "null")
        if not isinstance(first, dict):
            return None
        if first.get("type") == "error":
            return first.get("text") or "refused by hub"
        if first.get("type") != "welcome":
            return "unexpected first message from hub"
        self.key = first.get("key")
        self._send(sock, self._replay())
        self.last_rejection = None
        sock.settimeout(None)
        self._set_connected(True)

        last_read = [time.monotonic()]
        gone = threading.Event()
        threading.Thread(target=self._read_loop, args=(reader, last_read, gone), daemon=True).start()
        last_write = time.monotonic()
        while not self._stop.is_set() and not gone.is_set():
            self._wake.wait(1.0)
            self._wake.clear()
            now = time.monotonic()
            if now - last_read[0] > DEAD_AFTER:
                break
            self._idle.clear()
            try:
                batch = self._pull()
                if not batch and now - last_write > PING_INTERVAL:
                    batch = [{"type": "ping"}]
                if batch:
                    self._send(sock, batch)
                    last_write = now
            finally:
                self._idle.set()
        return None

    def _read_loop(self, reader, last_read, gone):
        try:
            while True:
                line = reader.readline(MAX_HUB_LINE)
                if not line:
                    return
                last_read[0] = time.monotonic()
                if not line.endswith("\n"):
                    # over-long line: skip the rest of it
                    while line and not line.endswith("\n"):
                        line = reader.readline(MAX_HUB_LINE)
                    continue
                try:
                    m = json.loads(line)
                except ValueError:
                    continue
                if isinstance(m, dict):
                    try:
                        self._dispatch(m)
                    except Exception:
                        pass
        except Exception:  # dropped (or a bug): the writer notices and reconnects
            pass
        finally:
            try:
                reader.close()
            except Exception:
                pass
            gone.set()
            self._wake.set()

    def _dispatch(self, m):
        t = m.get("type")
        if t == "response":
            with self._lock:
                slot = self._pending.pop(m.get("corr"), None)
            if slot:
                err = m.get("error")
                slot[1] = Response(err is None, m.get("result"), err)
                slot[0].set()
        elif t == "request":
            threading.Thread(target=self._answer, args=(m,), daemon=True).start()

    def _answer(self, req):
        resp = {"type": "response", "corr": req.get("id")}
        try:
            name = req.get("name") or ""
            args = req.get("args")
            if name == ACTION_REQUEST:
                a = args if isinstance(args, dict) else {}
                widget = a.get("widget")
                handler = self._actions.get(widget) if isinstance(widget, str) else None
                if handler is None:
                    raise LookupError("no action handler for '%s'" % widget)
                result = handler(a.get("value"))
            else:
                name = name if isinstance(name, str) else str(name)
                entry = self._provides.get(name)
                if entry is None and name.lower().startswith(self.app.lower() + "."):
                    entry = self._provides.get(name[len(self.app) + 1:])
                if entry is None:
                    raise LookupError("%s does not provide '%s'" % (self.app, name))
                result = entry[0](args)
            if result is not None:
                resp["result"] = self._plain(result)
        except Exception as ex:  # a handler's failure is the requester's answer
            resp["error"] = str(ex) or type(ex).__name__
        with self._lock:
            if not self._live:
                return
            self._out.append(resp)
        self._wake.set()

    def _set_connected(self, value):
        if self._connected == value:
            return
        self._connected = value
        if not value:
            with self._lock:
                self._live = False
                pending, self._pending = self._pending, {}
            for slot in pending.values():
                slot[1] = Response(False, error="hub connection lost")
                slot[0].set()


def _finite(v):
    """NaN and infinities have no JSON spelling: make them None, all the way down."""
    if isinstance(v, float):
        return v if math.isfinite(v) else None
    if isinstance(v, list):
        return [_finite(x) for x in v]
    if isinstance(v, dict):
        return {k: _finite(x) for k, x in v.items()}
    return v


def _dump(m):
    return json.dumps(m, separators=(",", ":"), default=str, allow_nan=False)


def _lines(m, max_chars=MAX_LINE):
    """
    m as wire lines within max_chars (the hub drops longer ones). An update too big for one line becomes
    several adding up to the same (a split stream replacement continues as appends); a single item too
    big on its own is left out. A response too big becomes an error response.
    """
    line = _dump(m)
    if len(line) <= max_chars:
        return [line]
    if m.get("type") == "response":
        return [_dump({"type": "response", "corr": m.get("corr"),
                       "error": "result too large to send (over %d characters)" % max_chars})]
    if m.get("type") != "update":
        return [line]
    budget = max_chars - 512
    out, chunk, size = [], None, 0

    def room(need):
        nonlocal chunk, size
        if chunk is not None and size + need > budget:
            out.append(_dump(chunk))
            chunk = None
        if chunk is None:
            chunk, size = {"type": "update"}, 0
        size += need
        return chunk

    def cost(id, item):
        return len(_dump(id)) + len(_dump(item)) + 8

    for id, items in (m.get("streams") or {}).items():
        if not items:
            room(len(id) + 16).setdefault("streams", {})[id] = []
            continue
        first = True
        for item in items:
            c = cost(id, item)
            if c > budget:
                continue
            into = room(c)
            kind = "streams" if first or id in into.get("streams", {}) else "append"
            into.setdefault(kind, {}).setdefault(id, []).append(item)
            first = False
    for id, value in (m.get("values") or {}).items():
        c = cost(id, value)
        if c <= budget:
            room(c).setdefault("values", {})[id] = value
    for id, items in (m.get("append") or {}).items():
        for item in items:
            c = cost(id, item)
            if c <= budget:
                room(c).setdefault("append", {}).setdefault(id, []).append(item)
    if chunk is not None:
        out.append(_dump(chunk))
    return out
