"""Friendly-name lookup for MadMapper OSC addresses (addresses.json)."""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path


class AddressError(KeyError):
    """Raised when a friendly name has no mapping."""

    def __str__(self) -> str:  # KeyError repr-quotes its message by default
        return str(self.args[0])


def _entries(section: dict | None) -> dict[str, str]:
    """Drop comment keys (anything starting with "_") and normalise names."""
    if not section:
        return {}
    return {
        name.strip().lower(): value
        for name, value in section.items()
        if not name.startswith("_")
    }


@dataclass
class AddressBook:
    surfaces: dict[str, str] = field(default_factory=dict)
    triggers: dict[str, str] = field(default_factory=dict)
    parameters: dict[str, str] = field(default_factory=dict)
    # "raw" sends values in the shader's own range (e.g. sensitivity 0-4);
    # "normalized" rescales them to 0-1 for hosts whose OSC sliders expect that.
    parameter_scale: str = "raw"

    @classmethod
    def from_dict(cls, data: dict) -> "AddressBook":
        return cls(
            surfaces=_entries(data.get("surfaces")),
            triggers=_entries(data.get("triggers")),
            parameters=_entries(data.get("parameters")),
            parameter_scale=data.get("parameter_scale", "raw"),
        )

    @classmethod
    def load(cls, path: str | Path) -> "AddressBook":
        with open(path, encoding="utf-8") as f:
            return cls.from_dict(json.load(f))

    def opacity_address(self, surface: str) -> str:
        """Resolve a surface to its opacity address.

        Accepts a friendly name from addresses.json, or falls back to treating
        the input as the MadMapper surface name itself.
        """
        target = self.surfaces.get(surface.strip().lower(), surface.strip())
        if target.startswith("/"):
            return target
        return f"/surfaces/{target}/opacity"

    def trigger_address(self, name: str) -> str:
        key = name.strip().lower()
        if key not in self.triggers:
            known = ", ".join(sorted(self.triggers)) or "none"
            raise AddressError(f"Unknown trigger '{name}'. Known triggers: {known}")
        return self.triggers[key]

    def parameter_address(self, name: str) -> str:
        key = name.strip().lower()
        if key not in self.parameters:
            known = ", ".join(sorted(self.parameters)) or "none"
            raise AddressError(f"Unknown parameter '{name}'. Known parameters: {known}")
        return self.parameters[key]

    def describe(self) -> str:
        """Human-readable summary, used in the agent's system prompt."""
        surfaces = ", ".join(sorted(self.surfaces)) or "(none configured)"
        triggers = ", ".join(sorted(self.triggers)) or "(none configured)"
        return f"Surfaces: {surfaces}\nTriggers: {triggers}"
