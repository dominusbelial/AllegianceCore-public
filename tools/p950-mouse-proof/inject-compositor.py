#!/usr/bin/env python3
"""Local GNOME Wayland input fixture; requires python3-dbus and libei.

Run beside the proof's `compositor` mode, passing its redirected log path.
Creates a local compositor virtual mouse, focuses only the named proof window,
then delivers 20 relative motions. No network connection or RDP session.
"""
import ctypes as c
from pathlib import Path
import select
import sys
import time

import dbus

log = Path(sys.argv[1])


def wait_for(marker):
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        if log.exists() and marker in log.read_text(errors="replace"):
            return
        time.sleep(.1)
    raise TimeoutError("Window did not reach " + marker)


def api(library, name, result, *args):
    function = getattr(library, name)
    function.restype, function.argtypes = result, args
    return function


# Discover the client position without assuming a monitor size or window placement.
x = c.CDLL("libX11.so.6")
P, U, I, X = c.c_void_p, c.c_uint, c.c_int, c.c_ulong
display = api(x, "XOpenDisplay", P, c.c_char_p)(None)
if not display:
    raise RuntimeError("X11 display unavailable")
root = api(x, "XDefaultRootWindow", X, P)(display)
tree = api(x, "XQueryTree", I, P, X, c.POINTER(X), c.POINTER(X), c.POINTER(P), c.POINTER(U))
fetch = api(x, "XFetchName", I, P, X, c.POINTER(P))
free = api(x, "XFree", I, P)
geometry = api(x, "XGetGeometry", I, P, X, c.POINTER(X), c.POINTER(I), c.POINTER(I),
               c.POINTER(U), c.POINTER(U), c.POINTER(U), c.POINTER(U))
translate = api(x, "XTranslateCoordinates", I, P, X, X, I, I, c.POINTER(I), c.POINTER(I), c.POINTER(X))


def find_window(window, depth=0):
    parent, root_out, children, count = X(), X(), P(), U()
    matches = []
    if depth < 3 and tree(display, window, c.byref(root_out), c.byref(parent), c.byref(children), c.byref(count)):
        for child in c.cast(children, c.POINTER(X))[:count.value]:
            matches.extend(find_window(child, depth + 1))
        if children:
            free(children)
    name = P()
    if fetch(display, window, c.byref(name)) and name:
        if c.string_at(name) == b"Allegiance ClientCore MonoGame (Mouse pilot)":
            matches.append((depth, window))
        free(name)
    return matches


wait_for("[p950] pointer setup")
matches = find_window(root)
deadline = time.monotonic() + 45
while not matches and time.monotonic() < deadline:
    time.sleep(.1)
    matches = find_window(root)
if not matches:
    raise RuntimeError("Proof window not found; refusing to inject into another application")
window = max(matches)[1]
xo, yo, width, height, border, bits, root_out = I(), I(), U(), U(), U(), U(), X()
geometry(display, window, c.byref(root_out), c.byref(xo), c.byref(yo), c.byref(width), c.byref(height), c.byref(border), c.byref(bits))
target_x, target_y, child = I(), I(), X()
translate(display, window, root, width.value // 2, height.value // 2, c.byref(target_x), c.byref(target_y), c.byref(child))
api(x, "XCloseDisplay", I, P)(display)

lib = c.CDLL("libei.so.1")
new = api(lib, "ei_new_sender", P, P)
configure = api(lib, "ei_configure_name", None, P, c.c_char_p)
setup = api(lib, "ei_setup_backend_fd", I, P, I)
dispatch = api(lib, "ei_dispatch", None, P)
fdof = api(lib, "ei_get_fd", I, P)
get = api(lib, "ei_get_event", P, P)
event_type = api(lib, "ei_event_get_type", I, P)
type_name = api(lib, "ei_event_type_to_string", c.c_char_p, I)
seat = api(lib, "ei_event_get_seat", P, P)
device = api(lib, "ei_event_get_device", P, P)
unref_event = api(lib, "ei_event_unref", P, P)
bind = api(lib, "ei_seat_bind_capabilities", None, P)  # varargs, zero terminated
has = api(lib, "ei_device_has_capability", c.c_bool, P, I)
ref_device = api(lib, "ei_device_ref", P, P)
unref_device = api(lib, "ei_device_unref", P, P)
start = api(lib, "ei_device_start_emulating", None, P, U)
motion = api(lib, "ei_device_pointer_motion", None, P, c.c_double, c.c_double)
absolute = api(lib, "ei_device_pointer_motion_absolute", None, P, c.c_double, c.c_double)
button = api(lib, "ei_device_button_button", None, P, U, c.c_bool)
frame = api(lib, "ei_device_frame", None, P, c.c_ulonglong)
unref = api(lib, "ei_unref", P, P)

# The compositor's local D-Bus session supplies the EIS Unix socket. Request
# pointer/absolute/button capabilities only; never bind a keyboard.
bus = dbus.SessionBus()
service = "org.gnome.Mutter.RemoteDesktop"
session_path = bus.get_object(service, "/org/gnome/Mutter/RemoteDesktop").CreateSession(dbus_interface=service)
session = dbus.Interface(bus.get_object(service, session_path), service + ".Session")
session.Start()
context, relative = new(None), None
sent = False


def flush(dev):
    frame(dev, time.monotonic_ns() // 1000)
    dispatch(context)


try:
    configure(context, b"P950 local pointer proof")
    if setup(context, session.ConnectToEIS(dbus.Dictionary({}, signature="sv")).take()) != 0:
        raise RuntimeError("Could not connect compositor input socket")
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline and not sent:
        dispatch(context)
        while event := get(context):
            kind = type_name(event_type(event)).decode()
            if kind == "EI_EVENT_SEAT_ADDED":
                bind(seat(event), I(1), I(2), I(32), I(0))
            elif kind == "EI_EVENT_DEVICE_RESUMED":
                dev = device(event)
                if has(dev, 1):
                    relative = ref_device(dev)
                    start(dev, 1)
                if has(dev, 2) and relative:
                    start(dev, 2)
                    absolute(dev, target_x.value, target_y.value)
                    flush(dev)
                    time.sleep(.15)
                    button(dev, 272, True)  # BTN_LEFT: focus the proof window
                    flush(dev)
                    button(dev, 272, False)
                    flush(dev)
                    wait_for("SDL relative flag=1")
                    for _ in range(20):
                        motion(relative, 20., -10.)
                        flush(relative)
                        time.sleep(.05)
                    sent = True
            unref_event(event)
        select.select([fdof(context)], [], [], .05)
    if not sent:
        raise TimeoutError("Compositor did not resume the pointer devices")
    print("[p950-input] compositor virtual mouse: 20 motions of (+20, -10) pixels")
finally:
    if relative:
        unref_device(relative)
    unref(context)
    session.Stop()
