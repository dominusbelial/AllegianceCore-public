#!/usr/bin/env python3
"""Record ONE window (the game), not the desktop: xdg-desktop-portal ScreenCast (window source) -> PipeWire ->
GStreamer -> hardware HEVC on the Intel GPU (vapostproc + vah265enc) -> MP4.

  record-window.py OUT.mp4 [--qp 20] [--fps 60] [--forget]

First run shows GNOME's window picker (choose the game window); the portal's restore token is saved in
~/.config/allegiance-record-window.token so later runs record the same window without asking (--forget re-asks).
Stop with Ctrl+C or SIGTERM: the pipeline gets EOS so the MP4 is finalised.
"""
import argparse, os, signal, sys
import gi
gi.require_version("Gst", "1.0")
from gi.repository import Gio, GLib, Gst
try:
    gi.require_version("GLibUnix", "2.0"); from gi.repository import GLibUnix; signal_add = GLibUnix.signal_add
except (ValueError, ImportError):
    signal_add = GLib.unix_signal_add

TOKEN = os.path.expanduser("~/.config/allegiance-record-window.token")
ap = argparse.ArgumentParser()
ap.add_argument("out"); ap.add_argument("--qp", type=int, default=20); ap.add_argument("--fps", type=int, default=60)
ap.add_argument("--forget", action="store_true")
a = ap.parse_args()

Gst.init(None)
loop = GLib.MainLoop()
bus = Gio.bus_get_sync(Gio.BusType.SESSION)
sender = bus.get_unique_name()[1:].replace(".", "_")
portal = Gio.DBusProxy.new_sync(bus, 0, None, "org.freedesktop.portal.Desktop",
                                "/org/freedesktop/portal/desktop", "org.freedesktop.portal.ScreenCast")
counter = [0]

def request(method, args_fmt, args, on_response):
    """Portal calls answer through a Request object's Response signal; subscribe before calling."""
    counter[0] += 1
    token = f"arw{os.getpid()}_{counter[0]}"
    path = f"/org/freedesktop/portal/desktop/request/{sender}/{token}"
    def handler(_c, _s, _p, _i, _sig, params):
        bus.signal_unsubscribe(sub[0])
        code, results = params.unpack()
        if code != 0:
            print(f"portal {method} refused (code {code}) — picker cancelled?", flush=True); loop.quit(); return
        on_response(results)
    sub = [bus.signal_subscribe("org.freedesktop.portal.Desktop", "org.freedesktop.portal.Request", "Response",
                                path, None, 0, handler)]
    opts = dict(args[-1]); opts["handle_token"] = GLib.Variant("s", token)
    portal.call_sync(method, GLib.Variant(args_fmt, (*args[:-1], opts)), 0, -1, None)

session = [None]; pipeline = [None]

def created(results):
    session[0] = results["session_handle"]
    opts = {"types": GLib.Variant("u", 2),            # 2 = WINDOW
            "multiple": GLib.Variant("b", False),
            "cursor_mode": GLib.Variant("u", 1),      # 1 = hidden (the game draws its own reticle)
            "persist_mode": GLib.Variant("u", 2)}     # 2 = persist until revoked
    if not a.forget and os.path.exists(TOKEN):
        opts["restore_token"] = GLib.Variant("s", open(TOKEN).read().strip())
    request("SelectSources", "(oa{sv})", (session[0], opts), selected)

def selected(_results):
    request("Start", "(osa{sv})", (session[0], "", {}), started)

def started(results):
    if "restore_token" in results:
        with open(TOKEN, "w") as f: f.write(results["restore_token"])
    node, props = results["streams"][0]
    size = props.get("size")
    res, fds = portal.call_with_unix_fd_list_sync("OpenPipeWireRemote",
                                                  GLib.Variant("(oa{sv})", (session[0], {})), 0, -1, None, None)
    fd = fds.get(res.unpack()[0])
    desc = (f"pipewiresrc fd={fd} path={node} do-timestamp=true keepalive-time=1000 always-copy=true "
            f"! videorate name=rate ! video/x-raw,framerate={a.fps}/1 ! vapostproc ! video/x-raw(memory:VAMemory),format=NV12 "
            f"! vah265enc rate-control=cqp qpi={a.qp} qpp={a.qp} qpb={a.qp} ! h265parse "
            f"! mp4mux fragment-duration=2000 ! filesink location={a.out}")
    p = Gst.parse_launch(desc); pipeline[0] = p
    b = p.get_bus(); b.add_signal_watch()
    def on_msg(_b, m):
        if m.type == Gst.MessageType.EOS:
            print("finalised", a.out, flush=True); p.set_state(Gst.State.NULL); loop.quit()
        elif m.type == Gst.MessageType.ERROR:
            err, dbg = m.parse_error(); print("ERROR", err.message, dbg, flush=True); p.set_state(Gst.State.NULL); loop.quit()
    b.connect("message", on_msg)
    p.set_state(Gst.State.PLAYING)
    print(f"recording window node {node} size {size} -> {a.out} (HEVC on the GPU, qp {a.qp}, {a.fps} fps)", flush=True)

def force_quit():
    if pipeline[0] is not None: pipeline[0].set_state(Gst.State.NULL)
    print("stopped", a.out, flush=True); loop.quit(); return GLib.SOURCE_REMOVE

def stop(*_):
    print("stopping (EOS)", flush=True)
    if pipeline[0] is not None:
        # pipewiresrc does not forward a pipeline EOS reliably: inject it right after the source.
        pipeline[0].get_by_name("rate").get_static_pad("sink").send_event(Gst.Event.new_eos())
        GLib.timeout_add_seconds(10, force_quit)   # fragmented MP4 is valid even if EOS never reaches the bus
    else: loop.quit()
    return GLib.SOURCE_REMOVE

signal_add(GLib.PRIORITY_DEFAULT, signal.SIGINT, stop)
signal_add(GLib.PRIORITY_DEFAULT, signal.SIGTERM, stop)
request("CreateSession", "(a{sv})", ({"session_handle_token": GLib.Variant("s", f"arws{os.getpid()}")},), created)
loop.run()
