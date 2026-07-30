#!/usr/bin/env python3
"""SPIKE — a minimal org.kde.StatusNotifierWatcher, standing in for the GNOME
AppIndicator extension / the KDE Plasma tray.

Its only job is to answer one question: when a StatusNotifierWatcher IS present on
the session bus, does an Avalonia TrayIcon actually register with it? Registrations
are printed so the CI job can assert on them.

Throwaway. Deleted with the rest of the spike (O-view issue #75).
"""
import sys

import dbus
import dbus.service
from dbus.mainloop.glib import DBusGMainLoop
from gi.repository import GLib

IFACE = "org.kde.StatusNotifierWatcher"


class Watcher(dbus.service.Object):
    def __init__(self, bus_name):
        super().__init__(bus_name, "/StatusNotifierWatcher")
        self.items = []

    @dbus.service.method(IFACE, in_signature="s", sender_keyword="sender")
    def RegisterStatusNotifierItem(self, service, sender=None):
        entry = service if service.startswith(":") or "." in service else sender
        print(f"[watcher] REGISTERED item service={service!r} sender={sender!r}", flush=True)
        self.items.append(entry)
        self.StatusNotifierItemRegistered(entry)

    @dbus.service.method(IFACE, in_signature="s")
    def RegisterStatusNotifierHost(self, service):
        print(f"[watcher] host registered: {service!r}", flush=True)

    @dbus.service.signal(IFACE, signature="s")
    def StatusNotifierItemRegistered(self, service):
        pass

    @dbus.service.method(dbus.PROPERTIES_IFACE, in_signature="ss", out_signature="v")
    def Get(self, interface, prop):
        return self.GetAll(interface).get(prop, "")

    @dbus.service.method(dbus.PROPERTIES_IFACE, in_signature="s", out_signature="a{sv}")
    def GetAll(self, interface):
        return {
            "RegisteredStatusNotifierItems": dbus.Array(self.items, signature="s"),
            "IsStatusNotifierHostRegistered": dbus.Boolean(True),
            "ProtocolVersion": dbus.Int32(0),
        }


def main():
    DBusGMainLoop(set_as_default=True)
    bus = dbus.SessionBus()
    name = dbus.service.BusName(IFACE, bus, do_not_queue=True)
    watcher = Watcher(name)
    print(f"[watcher] owning {IFACE}", flush=True)

    # Exit on a timeout so the CI step can never hang.
    seconds = int(sys.argv[1]) if len(sys.argv) > 1 else 30
    loop = GLib.MainLoop()
    GLib.timeout_add_seconds(seconds, loop.quit)
    loop.run()

    print(f"[watcher] exiting, {len(watcher.items)} item(s) registered: {watcher.items}", flush=True)


if __name__ == "__main__":
    main()
