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



def split_parameter(name: str, default_material: str | None = None) -> tuple[str | None, str]:
    """"GridSweep.progress" -> ("GridSweep", "progress"); bare names use the default."""
    if "." in name:
        material, param = name.split(".", 1)
        return material.strip(), param.strip()
    return default_material, name.strip()


def resolve_parameter(book: AddressBook, catalog, name: str, default_material: str | None = None):
    """Return (address, converter) for a parameter, checking it exists in the shader.

    The converter clamps a value to the shader's range and rescales it to 0-1
    when addresses.json sets parameter_scale to "normalized".
    """
    material, param = split_parameter(name, default_material)
    spec = None
    if material and catalog is not None and catalog.material_name(material):
        material = catalog.material_name(material)
        spec = catalog.spec(material, param)
        if spec is None:
            raise ValueError(f"{material} has no parameter '{param}'")
        param = spec.name
    address = book.parameter_address(param, material)

    def convert(value):
        if spec is None:
            return value
        value = spec.clamp(value)
        return spec.normalize(value) if book.parameter_scale == "normalized" else value

    return address, convert


def _fmt(value) -> str:
    if isinstance(value, float):
        return f"{value:g}"
    return str(value)


def set_parameter(osc: OscSender, book: AddressBook, catalog, name: str, value,
                  default_material: str | None = None) -> str:
    address, convert = resolve_parameter(book, catalog, name, default_material)
    sent = convert(value)
    osc.send(address, sent)
    return f"Set {address} to {_fmt(sent)}"


def apply_preset(osc: OscSender, book: AddressBook, catalog, library, name: str) -> str:
    preset = library.get(name)
    sent, missing = [], []
    for param, value in preset.values.items():
        try:
            set_parameter(osc, book, catalog, param, value, library.material)
            sent.append(param)
        except KeyError:
            missing.append(param)
    if not sent:
        raise ValueError(
            f"Preset '{preset.name}' sent nothing: none of its parameters have OSC "
            f"addresses in addresses.json ({', '.join(missing)})"
        )
    result = f"Applied preset '{preset.name}' ({len(sent)} parameters)"
    if missing:
        result += f"; skipped unmapped parameters: {', '.join(missing)}"
    return result
