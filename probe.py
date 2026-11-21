"""Attach the chat probe to the running LocalExile client and print what it sees.

Run from a LocalExile checkout with the client already in game:

    python probe.py --seconds 20
    python probe.py --keys        # focus the game, press Enter, type /he, Esc
    python probe.py --set-text "/he"  # write into the chat input (needs a captured chat object)

The probe uses the client-control Frida session: it attaches once, keeps the agent resident, and loads probe.js
beside the existing control script. Do not run a second Frida tool against the same client at the same time.
"""
import argparse
import ctypes
from ctypes import wintypes
from pathlib import Path
import sys
import time

import os

HERE = Path(__file__).resolve().parent


def find_client_control() -> Path | None:
    """LocalExile's scripts/dev: $LOCALEXILE_ROOT, else this repo or one above it."""
    if env := os.environ.get("LOCALEXILE_ROOT"):
        candidate = Path(env) / "LocalExile" / "scripts" / "dev"
        if candidate.is_dir():
            return candidate
    for base in (HERE, *HERE.parents):
        candidate = base / "LocalExile" / "scripts" / "dev"
        if (candidate / "client_control.py").exists():
            return candidate
    return None


client_control = find_client_control()
if client_control is None:
    raise SystemExit("LocalExile's scripts/dev not found; set LOCALEXILE_ROOT to the checkout that holds LocalExile/")
sys.path.insert(0, str(client_control))

from client_control import ClientControl  # noqa: E402

user32 = ctypes.WinDLL("user32", use_last_error=True)

ULONG_PTR = ctypes.c_ulonglong


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD), ("dwFlags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ULONG_PTR)]


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wintypes.LONG), ("dy", wintypes.LONG), ("mouseData", wintypes.DWORD),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD), ("dwExtraInfo", ULONG_PTR)]


class HARDWAREINPUT(ctypes.Structure):
    _fields_ = [("uMsg", wintypes.DWORD), ("wParamL", wintypes.WORD), ("wParamH", wintypes.WORD)]


class INPUT_UNION(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT), ("mi", MOUSEINPUT), ("hi", HARDWAREINPUT)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wintypes.DWORD), ("u", INPUT_UNION)]


KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_UNICODE = 0x0004
INPUT_KEYBOARD = 1


def send_input(*inputs):
    array = (INPUT * len(inputs))(*inputs)
    sent = user32.SendInput(len(inputs), array, ctypes.sizeof(INPUT))
    if sent != len(inputs):
        raise ctypes.WinError(ctypes.get_last_error())


def key(vk, up=False):
    scan = user32.MapVirtualKeyW(vk, 0)
    send_input(INPUT(type=INPUT_KEYBOARD,
                     u=INPUT_UNION(ki=KEYBDINPUT(wVk=vk, wScan=scan, dwFlags=KEYEVENTF_KEYUP if up else 0,
                                                time=0, dwExtraInfo=0))))


def unicode_char(ch, up=False):
    send_input(INPUT(type=INPUT_KEYBOARD,
                     u=INPUT_UNION(ki=KEYBDINPUT(wVk=0, wScan=ord(ch),
                                                dwFlags=KEYEVENTF_UNICODE | (KEYEVENTF_KEYUP if up else 0),
                                                time=0, dwExtraInfo=0))))


def tap(vk, delay=0.08):
    key(vk)
    time.sleep(delay)
    key(vk, up=True)
    time.sleep(delay)


WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_CHAR = 0x0102
SCANS = {0x0D: 0x1C, 0x1B: 0x01, 0xBF: 0x35, 0x48: 0x23, 0x45: 0x12, 0x23: 0x4F}


def post_key(hwnd, vk, ch=None, delay=0.08):
    scan = SCANS.get(vk, 0)
    down = 0x00000001 | (scan << 16)
    up = 0xC0000001 | (scan << 16)
    user32.PostMessageW(hwnd, WM_KEYDOWN, vk, down)
    if ch is not None:
        user32.PostMessageW(hwnd, WM_CHAR, ch, down)
    time.sleep(delay)
    user32.PostMessageW(hwnd, WM_KEYUP, vk, up)
    time.sleep(delay)


def type_text(text, delay=0.06):
    for ch in text:
        unicode_char(ch)
        time.sleep(0.03)
        unicode_char(ch, up=True)
        time.sleep(delay)


def windows_for(pid):
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def callback(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid:
            length = user32.GetWindowTextLengthW(hwnd)
            title = ctypes.create_unicode_buffer(length + 1)
            user32.GetWindowTextW(hwnd, title, length + 1)
            cls = ctypes.create_unicode_buffer(256)
            user32.GetClassNameW(hwnd, cls, 256)
            rect = wintypes.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(rect))
            found.append((hwnd, title.value, cls.value, user32.IsWindowVisible(hwnd),
                          (rect.right - rect.left) * (rect.bottom - rect.top)))
        return True

    user32.EnumWindows(callback, 0)
    return found


MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
SWP_NOSIZE = 0x0001
SWP_NOMOVE = 0x0002
SWP_SHOWWINDOW = 0x0040
HWND_TOPMOST = -1
HWND_NOTOPMOST = -2


def raise_window(hwnd):
    flags = SWP_NOSIZE | SWP_NOMOVE | SWP_SHOWWINDOW
    user32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags)
    user32.SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flags)


def click_window(hwnd):
    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    x = (rect.left + rect.right) // 2
    y = (rect.top + rect.bottom) // 2
    user32.SetCursorPos(x, y)
    time.sleep(0.1)
    user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.05)
    user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(0.4)


def focus_game(pid):
    candidates = [w for w in windows_for(pid) if w[3] and w[4] > 0]
    if not candidates:
        raise RuntimeError("no visible window for the client")
    hwnd, title, cls, _, area = max(candidates, key=lambda w: w[4])
    target = user32.GetWindowThreadProcessId(hwnd, None)
    # Windows only lets the foreground process change the foreground window; borrow the foreground thread's state.
    foreground = user32.GetForegroundWindow()
    foreground_thread = user32.GetWindowThreadProcessId(foreground, None) if foreground else 0
    if foreground_thread and foreground_thread != target:
        user32.AttachThreadInput(foreground_thread, target, True)
    user32.ShowWindow(hwnd, 9)  # SW_RESTORE
    user32.BringWindowToTop(hwnd)
    focused = user32.SetForegroundWindow(hwnd)
    user32.SetActiveWindow(hwnd)
    if foreground_thread and foreground_thread != target:
        user32.AttachThreadInput(foreground_thread, target, False)
    time.sleep(0.4)
    if user32.GetForegroundWindow() != hwnd:
        raise_window(hwnd)
        click_window(hwnd)
    foreground = user32.GetForegroundWindow()
    if not focused or foreground != hwnd:
        print(f"warning: could not focus {title!r} ({cls}); foreground is {foreground}", flush=True)
    return hwnd, title, cls, area


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    except (AttributeError, OSError):
        pass
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--seconds", type=float, default=15.0, help="how long to stay attached and print events")
    parser.add_argument("--keys", action="store_true", help="focus the game and type Enter /he Esc")
    parser.add_argument("--post", action="store_true", help="post Enter /he Esc as window messages")
    parser.add_argument("--complete", action="store_true", help="type /he, insert lp with the client's own insert function")
    parser.add_argument("--live", action="store_true", help="focus the game, press Enter, type /he, press Tab (native completion)")
    parser.add_argument("--set-text", help="write this into the chat input after attaching")
    parser.add_argument("--insert-text", help="insert this with the client's own insert function on the next frame")
    parser.add_argument("--insert-at", type=int, default=-1, help="insertion position; -1 is the end")
    parser.add_argument("--chat", help="chat object address captured by an earlier key event")
    parser.add_argument("--session", help="client session name (live.ps1 -Session)")
    args = parser.parse_args()

    control = ClientControl(args.session)
    control.attach()
    print(f"attached to pid {control.pid}", flush=True)

    script = control.session.create_script(Path(__file__).with_name("probe.js").read_text(encoding="utf-8"),
                                           name="chatcomplete-probe")
    script.on("message", lambda message, data: print(message.get("payload", message), flush=True))
    script.load()
    print("probe loaded", flush=True)

    if args.chat:
        print("useChat:", script.exports_sync.use_chat(args.chat), flush=True)

    if args.insert_text is not None:
        print("read:", script.exports_sync.read_on_frame(), flush=True)
        print("insert:", script.exports_sync.insert_on_frame(args.insert_text, args.insert_at), flush=True)
        print("read after:", script.exports_sync.read_on_frame(), flush=True)

    if args.keys:
        hwnd, title, cls, area = focus_game(control.pid)
        print(f"focused {title!r} ({cls}) {area}px", flush=True)
        tap(0x0D)          # Enter opens chat
        type_text("/he")
        time.sleep(1.0)
        tap(0x1B)          # Esc closes chat
    elif args.post:
        hwnd, title, cls, area = focus_game(control.pid)
        print(f"posting to {title!r} ({cls}) hwnd={hwnd}", flush=True)
        post_key(hwnd, 0x0D, 0x0D)
        post_key(hwnd, 0xBF, 0x2F)   # /
        post_key(hwnd, 0x48, 0x68)   # h
        post_key(hwnd, 0x45, 0x65)   # e
        time.sleep(1.0)
        print("state before Esc:", script.exports_sync.state(), flush=True)
        print("dump:", script.exports_sync.dump(), flush=True)
        post_key(hwnd, 0x1B, 0x1B)   # Esc
    elif args.live:
        hwnd, title, cls, area = focus_game(control.pid)
        print(f"live typing into {title!r} ({cls}) hwnd={hwnd}", flush=True)
        tap(0x0D)          # Enter opens chat
        time.sleep(0.4)
        tap(0xBF)          # /
        tap(0x48)          # h
        tap(0x45)          # e
        time.sleep(0.5)
        # The chat object is captured by the key hook; do not reuse a pointer from another client process.
        print("before Tab:", script.exports_sync.state(), flush=True)
        tap(0x09)          # Tab: the native DLL completes
        time.sleep(0.5)
        print("after Tab:", script.exports_sync.state(), flush=True)
        tap(0x1B)          # Esc
    elif args.complete:
        hwnd, title, cls, area = focus_game(control.pid)
        print(f"typing into {title!r} ({cls}) hwnd={hwnd}", flush=True)
        post_key(hwnd, 0x0D, 0x0D)   # Enter opens chat
        post_key(hwnd, 0xBF, None)   # /
        post_key(hwnd, 0x48, None)   # h
        post_key(hwnd, 0x45, None)   # e
        time.sleep(0.6)
        print("before:", script.exports_sync.state(), flush=True)
        print("queue:", script.exports_sync.queue_insert("lp"), flush=True)
        post_key(hwnd, 0x23, None)   # End: the key handler inserts the queued text first
        time.sleep(0.6)
        print("after:", script.exports_sync.state(), flush=True)
        post_key(hwnd, 0x1B, 0x1B)   # Esc
    elif args.set_text is not None:
        time.sleep(0.5)
        print("setText:", script.exports_sync.set_text(args.set_text), flush=True)
        time.sleep(0.3)
        print("state:", script.exports_sync.state(), flush=True)

    try:
        time.sleep(args.seconds)
    except KeyboardInterrupt:
        pass
    finally:
        # Frida's detach can wait on an injected hook; the probe is a one-shot dev tool.
        print("done", flush=True)
        sys.stdout.flush()
        import os
        os._exit(0)


if __name__ == "__main__":
    main()
