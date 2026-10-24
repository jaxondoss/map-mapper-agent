"""Command-line entry point: one-shot instruction or an interactive prompt."""

from __future__ import annotations

import argparse
import logging
import sys

import anthropic

from .addresses import AddressBook
from .agent import DEFAULT_MODEL, MadMapperAgent
from .osc import DEFAULT_HOST, DEFAULT_PORT, OscSender
from .materials import Catalog
from .presets import PresetLibrary
from .shows import ShowError, ShowLibrary, run_show


def _print_tool(name: str, inp: dict, output: str, is_error: bool) -> None:
    marker = "!" if is_error else "→"
    print(f"  {marker} {output}", file=sys.stderr)


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(
        prog="madmapper-agent",
        description="Control MadMapper over OSC with plain-English instructions.",
    )
    p.add_argument("instruction", nargs="*", help="Instruction to run. Omit for an interactive prompt.")
    p.add_argument("--host", default=DEFAULT_HOST, help=f"MadMapper machine (default {DEFAULT_HOST})")
    p.add_argument("--port", type=int, default=DEFAULT_PORT, help=f"MadMapper OSC input port (default {DEFAULT_PORT})")
    p.add_argument("--addresses", default="addresses.json", help="Path to addresses.json")
    p.add_argument("--presets", default="presets.json", help="Path to presets.json (optional)")
    p.add_argument("--materials", default="materials", help="Folder of .fs shader materials")
    p.add_argument("--shows", default="shows", help="Folder of show scripts")
    p.add_argument("--show", metavar="NAME", help="Play a show script directly (no Claude, no API key)")
    p.add_argument("--dry-run", action="store_true", help="Log OSC messages instead of sending them")
    p.add_argument("--model", default=DEFAULT_MODEL)
    p.add_argument("-v", "--verbose", action="store_true")
    args = p.parse_args(argv)

    logging.basicConfig(
        level=logging.INFO if (args.verbose or args.dry_run) else logging.WARNING,
        format="%(message)s",
    )
    for noisy in ("httpx", "httpx2", "anthropic"):
        logging.getLogger(noisy).setLevel(logging.WARNING)

    try:
        book = AddressBook.load(args.addresses)
    except FileNotFoundError:
        print(f"Can't find {args.addresses}. Copy the example and fill in your OSC addresses.", file=sys.stderr)
        return 2

    try:
        library = PresetLibrary.load(args.presets)
    except FileNotFoundError:
        library = None

    catalog = Catalog.load(args.materials)
    shows = ShowLibrary.load(args.shows)
    osc = OscSender(args.host, args.port, dry_run=args.dry_run)

    if args.show:
        try:
            show = shows.get(args.show)
            print(f"Playing '{show.name}' ({show.duration:.1f}s)...", file=sys.stderr)
            print(run_show(osc, book, show, catalog))
        except (ShowError, KeyError) as exc:
            print(f"Can't play show: {exc}", file=sys.stderr)
            return 1
        return 0

    agent = MadMapperAgent(
        osc, book, model=args.model, library=library, catalog=catalog, shows=shows,
        on_tool=_print_tool,
    )

    def run(text: str) -> None:
        try:
            print(agent.run(text))
        except anthropic.AuthenticationError:
            print("Anthropic API key missing or invalid. Set ANTHROPIC_API_KEY.", file=sys.stderr)
            raise SystemExit(1)
        except anthropic.APIConnectionError as exc:
            print(f"Couldn't reach the Anthropic API: {exc}", file=sys.stderr)
        except anthropic.APIStatusError as exc:
            print(f"Anthropic API error {exc.status_code}: {exc.message}", file=sys.stderr)

    if args.instruction:
        run(" ".join(args.instruction))
        return 0

    target = "dry run" if args.dry_run else f"{args.host}:{args.port}"
    print(f"MadMapper agent ({target}). Type an instruction, or 'quit'.")
    while True:
        try:
            text = input("> ").strip()
        except (EOFError, KeyboardInterrupt):
            print()
            return 0
        if text.lower() in {"quit", "exit"}:
            return 0
        if text:
            run(text)


if __name__ == "__main__":
    raise SystemExit(main())
