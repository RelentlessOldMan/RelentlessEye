"""
A pretend worker that shows every widget kind. Run it with or without the hub; start and stop the
hub while it runs and the panel catches up. Copy the parts you need.

    python examples/python/example_app.py [--hub otherpc:47800] [--token XXXX]

relentless_monitor.py lives in clients/python; copy it next to your own app.
"""

import argparse
import os
import random
import sys
import threading
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "clients", "python"))
import relentless_monitor as rm  # noqa: E402

parser = argparse.ArgumentParser()
parser.add_argument("--hub")
parser.add_argument("--token")
opts = parser.parse_args()

# 1. Describe the panel. This is all the monitor knows about this app.
mon = rm.MonitorClient("ExamplePy", rm.layout(
    rm.section("Job",
               rm.indicator("state", "State", states={"idle": "gray", "running": "green", "paused": "orange", "failed": "red"}),
               rm.text("stage", "Stage"),
               rm.progress("progress", "Progress", span=2),
               rm.kv("counts", "Counts", span=2)),
    rm.section("Numbers",
               rm.gauge("queue", "Queue depth", max=50),
               rm.chart("rate", "Items/s", limit=120, min=0)),
    rm.section("Log", rm.feed("log", limit=300), columns=1),
    rm.section("Controls",
               rm.button("restart", "Restart job"),
               rm.button("fail", "Simulate failure", confirm="Make the job fail?", color="red"),
               rm.toggle("paused", "Paused"),
               rm.choice("speed", "Speed", ["slow", "normal", rm.option("fast", color="orange", confirm="Run flat out?")], span=2),
               rm.flags("verbose", "Log extra", ["timings", "memory"]), columns=3),
    title="Example (Python)"),
    hub=opts.hub, token=opts.token, instance=str(os.getpid()), version="1.0")

# 2. Handle the panel's buttons, toggles, choices and flags.
state = {"paused": False, "restart": False, "fail": False, "restarts": 0, "pct": 0.0}


def on_restart(_):
    state["restart"] = True
    state["restarts"] += 1
    mon.log("log", "restart requested from the monitor", "warn")


def on_pause(on):
    state["paused"] = on
    mon.log("log", "paused" if on else "resumed")


mon.on_action("restart", on_restart)
mon.on_action("fail", lambda _: state.update(fail=True))
mon.on_toggle("paused", on_pause)
mon.on_choice("speed", lambda v: mon.log("log", "speed set to %s by %r" % (v, mon.sender)))
mon.on_flags("verbose", lambda on: mon.log("log", "extra logging: " + (", ".join(on) or "none")))
mon.set("speed", "normal")
mon.set("verbose", [])

# 3. Answer requests from other apps or tools: `rmon request ExamplePy.status`.
mon.provide("status", lambda args: {"paused": state["paused"], "progress": state["pct"], "restarts": state["restarts"]},
            "current job state")

mon.start()
print("ExamplePy running. Ctrl+C to stop.")
if mon.wait_connected(1.0):
    print("hub connected as", mon.key)
else:
    print("no hub yet (still running; will retry)")


# 4. Push values whenever they change. These never block, hub or no hub.
def numbers():
    while True:
        mon.set("queue", random.randint(0, 50))
        mon.set("rate", round(random.uniform(20, 80), 1))
        time.sleep(1)


threading.Thread(target=numbers, daemon=True).start()

stages = ["scanning", "parsing", "analyzing", "writing"]
job = 0
try:
    while True:
        job += 1
        state["restart"] = False
        mon.set_many({"state": "running", "paused": state["paused"]})
        mon.log("log", "job %d started" % job)
        outcome = "done"
        for step in range(101):
            if state["paused"]:
                mon.set("state", "paused")
            while state["paused"]:
                time.sleep(0.2)
            if state["restart"]:
                outcome = "restarted"
                break
            if state["fail"]:
                state["fail"] = False
                outcome = "failed"
                break
            state["pct"] = step / 100
            stage = stages[min(step // 25, len(stages) - 1)]
            mon.set_many({
                "state": "running",
                "stage": stage,
                "progress": {"value": step / 100, "text": "%d%% (%s)" % (step, stage)},
                "counts": {"job": job, "files": step * 12, "restarts": state["restarts"]},
            })
            if step % 20 == 0:
                mon.log("log", "job %d: %s (%d%%)" % (job, stage, step))
            time.sleep(0.15)
        if outcome == "failed":
            mon.set("state", "failed")
            mon.log("log", "job %d failed (simulated)" % job, "error")
            time.sleep(3)
        elif outcome == "done":
            mon.log("log", "job %d done" % job)
            mon.set("state", "idle")
            time.sleep(1.5)
except KeyboardInterrupt:
    pass
finally:
    mon.set("state", "idle")
    mon.log("log", "ExamplePy stopping")
    mon.close()
