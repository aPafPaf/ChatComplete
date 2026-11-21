"""Load NativeSuggest.dll into the running LocalExile client through the client-control Frida session.

Developer fallback for a checkout that has this repository beside LocalExile, for example
<root>/Tools/ChatComplete with <root>/LocalExile. Players install the mod instead.
"""
import json
import os
import subprocess
import sys
import time
import argparse
from pathlib import Path

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

DLL = HERE / "native" / "build" / "Release" / "NativeSuggest.dll"
EXPORT_ARGS = HERE / "tools" / "export_args.py"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dll", default=str(DLL))
    parser.add_argument("--no-args", action="store_true", help="do not regenerate the command argument catalogs")
    args = parser.parse_args()
    dll = Path(args.dll)
    if not dll.exists():
        raise SystemExit(f"missing {dll}; build native first")
    if not args.no_args and EXPORT_ARGS.exists():
        # Argument catalogs (mob names for /spawn_mob) live in %TEMP%.
        subprocess.run([sys.executable, str(EXPORT_ARGS)], check=False)
    control = ClientControl()
    control.attach()
    print(f"attached to pid {control.pid}", flush=True)
    source = f"const m = Module.load({json.dumps(str(dll))}); send({{kind:'loaded', module:m}});"
    script = control.session.create_script(source, name="chatcomplete-loader")
    script.on("message", lambda message, data: print(message.get("payload", message), flush=True))
    script.load()
    time.sleep(1.0)
    print("DLL loaded; check %TEMP%\\NativeSuggest.log", flush=True)
    control.close()


if __name__ == "__main__":
    main()
