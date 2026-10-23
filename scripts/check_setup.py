#!/usr/bin/env python3
"""Check that this machine is ready to run madmapper-agent.

Usage: python scripts/check_setup.py [--addresses addresses.json] [--host H] [--port P]

Failures mean the agent won't run. Warnings are things to look at before a
live show (e.g. no API key yet, or OSC addresses still at their example values).
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

EXAMPLE_TRIGGERS = {"/scenes/Intro/select", "/master/blackout"}

failures: list[str] = []
warnings: list[str] = []


def ok(msg: str) -> None:
    print(f"  ok    {msg}")


def warn(msg: str) -> None:
    warnings.append(msg)
    print(f"  WARN  {msg}")


def fail(msg: str) -> None:
    failures.append(msg)
    print(f"  FAIL  {msg}")


def check_python() -> None:
    if sys.version_info >= (3, 10):
        ok(f"Python {sys.version.split()[0]}")
    else:
        fail(f"Python 3.10+ required, found {sys.version.split()[0]}")


def check_imports() -> None:
    for module, package in (("anthropic", "anthropic"), ("pythonosc", "python-osc")):
        try:
            __import__(module)
            ok(f"{package} installed")
        except ImportError:
            fail(f"{package} not installed (pip install -e .)")


def check_api_key() -> None:
    if os.environ.get("ANTHROPIC_API_KEY") or os.environ.get("ANTHROPIC_AUTH_TOKEN"):
        ok("Anthropic credentials found in environment")
    else:
        warn("ANTHROPIC_API_KEY is not set (needed for live runs; --dry-run still calls Claude)")


def check_addresses(path: Path) -> None:
    from madmapper_agent.addresses import AddressBook

    if not path.exists():
        fail(f"{path} not found")
        return
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        book = AddressBook.from_dict(data)
    except (json.JSONDecodeError, AttributeError) as exc:
        fail(f"{path} is not valid: {exc}")
        return
    ok(f"{path.name}: {len(book.surfaces)} surfaces, {len(book.triggers)} triggers")

    if not book.surfaces and not book.triggers:
        warn("addresses.json has no surfaces or triggers yet")
    for name in book.surfaces:
        address = book.opacity_address(name)
        if " " in address:
            warn(f"surface '{name}' -> '{address}' contains a space; check it against Copy OSC Address")
    for name, address in book.triggers.items():
        if not address.startswith("/"):
            fail(f"trigger '{name}' address must start with '/': {address!r}")
    if EXAMPLE_TRIGGERS & set(book.triggers.values()):
        warn("addresses.json still has example trigger addresses; replace them with ones copied from MadMapper")


def check_udp(host: str, port: int) -> None:
    try:
        socket.getaddrinfo(host, port, type=socket.SOCK_DGRAM)
    except socket.gaierror as exc:
        fail(f"can't resolve host {host}: {exc}")
        return
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.connect((host, port))
        ok(f"UDP route to {host}:{port} (UDP can't confirm MadMapper is listening; do a live test)")
    except OSError as exc:
        fail(f"no UDP route to {host}:{port}: {exc}")


def main() -> int:
    from madmapper_agent.osc import DEFAULT_HOST, DEFAULT_PORT

    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--addresses", default=str(ROOT / "addresses.json"))
    p.add_argument("--host", default=DEFAULT_HOST)
    p.add_argument("--port", type=int, default=DEFAULT_PORT)
    args = p.parse_args()

    print("madmapper-agent setup check")
    check_python()
    check_imports()
    check_api_key()
    if not failures:
        check_addresses(Path(args.addresses))
    check_udp(args.host, args.port)

    print(f"\n{len(failures)} failure(s), {len(warnings)} warning(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
