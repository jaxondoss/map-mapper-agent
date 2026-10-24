"""Timed show scripts (shows/*.json): cues that fade surfaces, ramp shader
parameters and fire triggers on a timeline.

A show file looks like:

    {
      "description": "...",
      "surfaces": {"grid": {"material": "GridSweep"}},
      "steps": [
        {"at": 0.0, "opacity": "grid", "to": 1},
        {"at": 0.0, "set": "GridSweep.progress", "from": 0, "to": 1, "seconds": 2.4},
        {"at": 3.0, "trigger": "intro scene"},
        {"at": 4.0, "osc": "/some/address", "args": [1]}
      ]
    }

"set" steps name a parameter as Material.param. With "seconds" a step ramps
from "from" (or the value the show last sent) to "to"; without it, it jumps.
"""

from __future__ import annotations

import json
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

from . import actions
from .addresses import AddressBook
from .osc import OscSender

RAMP_RATE_HZ = 30
STEP_KINDS = ("opacity", "set", "trigger", "osc")


class ShowError(ValueError):
    pass


@dataclass
class Event:
    time: float
    address: str
    value: Any


@dataclass
class Show:
    name: str
    description: str = ""
    surfaces: dict[str, dict] = field(default_factory=dict)
    steps: list[dict] = field(default_factory=list)

    @classmethod
    def load(cls, path: str | Path) -> "Show":
        path = Path(path)
        data = json.loads(path.read_text(encoding="utf-8"))
        surfaces = {k: v for k, v in data.get("surfaces", {}).items() if not k.startswith("_")}
        return cls(path.stem, data.get("description", ""), surfaces, list(data.get("steps", [])))

    @property
    def duration(self) -> float:
        return max((float(s.get("at", 0)) + float(s.get("seconds", 0)) for s in self.steps), default=0.0)

    def compile(self, book: AddressBook, catalog=None) -> list[Event]:
        """Turn the steps into a time-sorted list of OSC messages.

        Raises ShowError naming the step if anything can't be resolved, so a
        broken show fails before it starts rather than halfway through.
        """
        events: list[Event] = []
        last: dict[str, Any] = {}       # address -> last value this show sent

        for n, step in enumerate(sorted(self.steps, key=lambda s: float(s.get("at", 0))), 1):
            kinds = [k for k in STEP_KINDS if k in step]
            if len(kinds) != 1:
                raise ShowError(f"step {n}: needs exactly one of {', '.join(STEP_KINDS)}")
            kind = kinds[0]
            at = float(step.get("at", 0))
            try:
                if kind == "trigger":
                    events.append(Event(at, book.trigger_address(step["trigger"]), step.get("value", 1.0)))
                    continue
                if kind == "osc":
                    address = step["osc"]
                    if not address.startswith("/"):
                        raise ShowError("OSC address must start with '/'")
                    events.append(Event(at, address, list(step.get("args", []))))
                    continue
                if kind == "opacity":
                    address = book.opacity_address(step["opacity"])
                    convert = lambda v: max(0.0, min(1.0, float(v)))  # noqa: E731
                else:
                    address, convert = actions.resolve_parameter(book, catalog, step["set"])
                if "to" not in step:
                    raise ShowError("missing 'to'")
            except (KeyError, ValueError) as exc:
                raise ShowError(f"step {n} ({kind} at {at:g}s): {exc}") from None

            to = step["to"]
            seconds = float(step.get("seconds", 0))
            if seconds <= 0:
                events.append(Event(at, address, convert(to)))
                last[address] = to
                continue

            start = step.get("from", last.get(address, 0.0 if kind == "opacity" else None))
            if start is None:
                raise ShowError(
                    f"step {n} ({kind} at {at:g}s): ramp needs 'from' because the show "
                    "hasn't set this parameter yet"
                )
            if not all(isinstance(v, (int, float)) for v in (start, to)):
                raise ShowError(f"step {n} ({kind} at {at:g}s): only numbers can ramp")
            frames = max(1, round(seconds * RAMP_RATE_HZ))
            for i in range(frames + 1):
                f = i / frames
                events.append(Event(at + seconds * f, address, convert(start + (to - start) * f)))
            last[address] = to

        events.sort(key=lambda e: e.time)   # stable: same-time events keep step order
        return events


def run_show(
    osc: OscSender,
    book: AddressBook,
    show: Show,
    catalog=None,
    clock: Callable[[], float] = time.monotonic,
) -> str:
    """Play a show in real time. Blocks until the last cue is sent."""
    events = show.compile(book, catalog)
    start = clock()
    for ev in events:
        delay = start + ev.time - clock()
        if delay > 0:
            osc.sleep(delay)
        osc.send(ev.address, ev.value)
    return f"Played show '{show.name}': {len(events)} messages over {show.duration:.1f}s"


@dataclass
class ShowLibrary:
    shows: dict[str, Show] = field(default_factory=dict)

    @classmethod
    def load(cls, folder: str | Path) -> "ShowLibrary":
        folder = Path(folder)
        if not folder.is_dir():
            return cls()
        return cls({p.stem.lower(): Show.load(p) for p in sorted(folder.glob("*.json"))})

    def get(self, name: str) -> Show:
        key = name.strip().lower().replace(" ", "_")
        if key not in self.shows:
            known = ", ".join(s.name for s in self.shows.values()) or "none"
            raise ShowError(f"Unknown show '{name}'. Known shows: {known}")
        return self.shows[key]

    def describe(self) -> str:
        if not self.shows:
            return "Shows: (none loaded)"
        lines = ["Shows (timed scripts):"]
        lines += [f"- {s.name} ({s.duration:.0f}s): {s.description}" for s in self.shows.values()]
        return "\n".join(lines)
