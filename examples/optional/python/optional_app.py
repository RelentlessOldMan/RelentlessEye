"""
An app that feeds RelentlessEye only if it's installed: just monitor_optional.py copied in.
Runs the same with or without relentless_eye.py or a hub.

    python examples/optional/python/optional_app.py
"""

import os
import threading
import time

from monitor_optional import monitor

mon = monitor("OptionalApp.py", {
    "title": "Optional app (Python)",
    "sections": [
        {"title": "Work", "widgets": [
            {"id": "count", "kind": "text", "label": "Items done"},
            {"id": "rate", "kind": "chart", "label": "Items/s", "min": 0, "limit": 60},
            {"id": "log", "kind": "feed", "span": 2}]},
        {"title": "Controls", "columns": 2, "widgets": [
            {"id": "reset", "kind": "button", "label": "Reset count"},
            {"id": "fast", "kind": "toggle", "label": "Fast"}]},
    ],
}, instance=str(os.getpid()))

print("OptionalApp.py running (%s). Ctrl+C to stop." % mon.status)

# The app's own logic never depends on the monitor.
lock = threading.Lock()
count = 0
fast = False


def reset(_):
    global count
    with lock:
        count = 0
    mon.log("log", "count reset from the monitor", "warn")


def set_fast(on):
    global fast
    fast = on


mon.on_action("reset", reset)
mon.on_toggle("fast", set_fast)
mon.provide("count", lambda _: count, "items done so far")
mon.start()

try:
    while True:
        batch = 20 if fast else 5
        with lock:
            count += batch
            now = count
        mon.set("count", now)
        mon.set("rate", batch)
        if now % 50 == 0:
            mon.log("log", "reached %d" % now)
        time.sleep(1)
except KeyboardInterrupt:
    pass
finally:
    mon.close()
    print("stopped at %d" % count)
