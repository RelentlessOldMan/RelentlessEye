"""
monitor_optional.py - copy this one file into your app to feed RelentlessEye only if it's there.

If relentless_eye.py can be imported, your app shows up in the monitor. If it can't (or no hub is
running), every call does nothing - your app runs exactly the same.

Where it looks for relentless_eye.py, first match wins:
  1. your normal import path (e.g. you copied it next to your script)
  2. %RELENTLESSEYE_CLIENT%                  (the folder it's in)
  3. %LOCALAPPDATA%\\RelentlessEye\\client     (publish.ps1 -InstallClient puts it there)

    from monitor_optional import monitor

    mon = monitor("MyApp", {"title": "My App", "sections": [
        {"title": "Job", "widgets": [
            {"id": "stage", "kind": "text", "label": "Stage"},
            {"id": "cancel", "kind": "button", "label": "Cancel"}]}]})
    mon.on_action("cancel", lambda _: cancel())   # handlers before start() so the hub lists them
    mon.start()
    mon.set("stage", "parsing")

The layout is a plain dict in the shape docs/PROTOCOL.md describes, so nothing in your app needs
the real module.
"""

import importlib
import os
import sys


def _find():
    try:
        return importlib.import_module("relentless_eye"), "import path"
    except ImportError:
        pass
    folders = [os.environ.get("RELENTLESSEYE_CLIENT")]
    if os.environ.get("LOCALAPPDATA"):
        folders.append(os.path.join(os.environ["LOCALAPPDATA"], "RelentlessEye", "client"))
    for folder in folders:
        if folder and os.path.isfile(os.path.join(folder, "relentless_eye.py")):
            sys.path.append(folder)
            try:
                return importlib.import_module("relentless_eye"), folder
            except Exception:
                return None, None
    return None, None


class _Off:
    """Stands in for MonitorClient when the monitor isn't installed: every call does nothing."""
    connected = False
    enabled = False
    sender = None

    def __init__(self, status):
        self.status = status

    def request(self, *args, **kwargs):
        return None

    def get(self, *args, **kwargs):
        return None

    def __getattr__(self, name):
        return lambda *args, **kwargs: None

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        pass


def monitor(app, layout=None, **options):
    """
    Returns a MonitorClient (call .start()) if relentless_eye is available, otherwise a stand-in whose
    methods do nothing. `options` go to MonitorClient (hub, token, instance, version, ...).
    Check `.enabled` / `.status` to see which you got.
    """
    rm, where = _find()
    if rm is None:
        return _Off("relentless_eye.py not found")
    try:
        client = rm.MonitorClient(app, layout, **options)
    except Exception as e:  # the monitor must never break the app
        return _Off("monitor disabled: %s" % e)
    client.enabled = True
    client.status = "relentless_eye from %s" % where
    return client
