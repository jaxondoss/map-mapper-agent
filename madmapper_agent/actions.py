"""The actions the agent can take. Each returns a short result string."""

from __future__ import annotations

from .addresses import AddressBook
from .osc import OscArg, OscSender

FADE_RATE_HZ = 30
MAX_FADE_SECONDS = 600


def _clamp_opacity(value: float) -> float:
    return max(0.0, min(1.0, float(value)))


def set_opacity(osc: OscSender, book: AddressBook, surface: str, value: float) -> str:
    address = book.opacity_address(surface)
    value = _clamp_opacity(value)
    osc.send(address, value)
    return f"Set {address} to {value:.2f}"


def trigger(osc: OscSender, book: AddressBook, name: str, value: OscArg = 1.0) -> str:
    address = book.trigger_address(name)
    osc.send(address, value)
    return f"Triggered {address} ({value})"


def fade(
    osc: OscSender,
    book: AddressBook,
    surface: str,
    to_value: float,
    seconds: float,
    from_value: float | None = None,
) -> str:
    """Linear opacity fade. Blocks for `seconds` while it sends steps.

    MadMapper can't report the current opacity back over a one-way UDP link,
    so the start value defaults to the opposite end of the fade (0 when fading
    up, 1 when fading down) unless `from_value` is given.
    """
    address = book.opacity_address(surface)
    to_value = _clamp_opacity(to_value)
    if from_value is None:
        from_value = 0.0 if to_value >= 0.5 else 1.0
    from_value = _clamp_opacity(from_value)
    seconds = max(0.0, min(float(seconds), MAX_FADE_SECONDS))

    steps = max(1, round(seconds * FADE_RATE_HZ))
    interval = seconds / steps
    osc.send(address, from_value)
    for i in range(1, steps + 1):
        if interval:
            osc.sleep(interval)
        osc.send(address, from_value + (to_value - from_value) * i / steps)
    return f"Faded {address} from {from_value:.2f} to {to_value:.2f} over {seconds:g}s"


def send_raw(osc: OscSender, address: str, args: list[OscArg] | None = None) -> str:
    osc.send(address, args or [])
    return f"Sent {address} {args or []}"
