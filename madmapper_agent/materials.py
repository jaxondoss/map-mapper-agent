"""Shader materials (ISF .fs files) and the inputs they expose to MadMapper."""

from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from pathlib import Path

# Input types that can be driven over OSC (audio and image inputs are fed by MadMapper).
CONTROL_TYPES = ("float", "long", "bool", "point2D", "color")


@dataclass
class ShaderInput:
    name: str
    type: str
    min: float | list[float] = 0.0
    max: float | list[float] = 1.0
    values: list[int] = field(default_factory=list)  # for "long" (menu) inputs

    def clamp(self, value):
        if self.type == "long":
            v = int(round(value))
            if self.values and v not in self.values:
                raise ValueError(f"'{self.name}' must be one of {self.values}")
            return v
        if self.type == "bool":
            return bool(value)
        if self.type in ("point2D", "color"):
            if not isinstance(value, (list, tuple)):
                raise ValueError(f"'{self.name}' needs a list of numbers")
            lo = self.min if isinstance(self.min, list) else [0.0] * len(value)
            hi = self.max if isinstance(self.max, list) else [1.0] * len(value)
            return [max(a, min(b, float(v))) for v, a, b in zip(value, lo, hi)]
        return max(self.min, min(self.max, float(value)))

    def normalize(self, value):
        """Map an already-clamped value to 0-1 for hosts whose sliders expect that."""
        if self.type == "long" and self.values:
            return 0.0 if len(self.values) == 1 else self.values.index(value) / (len(self.values) - 1)
        if self.type == "bool":
            return 1.0 if value else 0.0
        if self.type == "point2D" and isinstance(self.min, list):
            return [(v - a) / (b - a) if b != a else 0.0 for v, a, b in zip(value, self.min, self.max)]
        if self.type == "float":
            span = self.max - self.min
            return 0.0 if span == 0 else (value - self.min) / span
        return value

    def describe(self) -> str:
        if self.type == "long" and self.values:
            return f"{self.name} ({self.values[0]}-{self.values[-1]})"
        if self.type == "float":
            return f"{self.name} ({self.min:g}-{self.max:g})"
        return f"{self.name} ({self.type})"


def read_isf_inputs(path: str | Path) -> dict[str, ShaderInput]:
    """Parse the JSON header of an ISF shader and return its controllable inputs."""
    text = Path(path).read_text(encoding="utf-8")
    m = re.search(r"/\*(.*?)\*/", text, re.S)
    if not m:
        raise ValueError(f"{path} has no ISF JSON header")
    inputs = {}
    for i in json.loads(m.group(1)).get("INPUTS", []):
        if i["TYPE"] not in CONTROL_TYPES:
            continue
        lo, hi = i.get("MIN", 0.0), i.get("MAX", 1.0)
        inputs[i["NAME"]] = ShaderInput(
            name=i["NAME"],
            type=i["TYPE"],
            min=[float(v) for v in lo] if isinstance(lo, list) else float(lo),
            max=[float(v) for v in hi] if isinstance(hi, list) else float(hi),
            values=list(i.get("VALUES", [])),
        )
    return inputs


@dataclass
class Catalog:
    """Every material in a folder, keyed by file stem (e.g. "GridSweep")."""

    materials: dict[str, dict[str, ShaderInput]] = field(default_factory=dict)

    @classmethod
    def load(cls, folder: str | Path) -> "Catalog":
        folder = Path(folder)
        if not folder.is_dir():
            return cls()
        return cls({p.stem: read_isf_inputs(p) for p in sorted(folder.glob("*.fs"))})

    def material_name(self, material: str) -> str | None:
        for name in self.materials:
            if name.lower() == material.strip().lower():
                return name
        return None

    def spec(self, material: str, param: str) -> ShaderInput | None:
        name = self.material_name(material)
        if name is None:
            return None
        for n, spec in self.materials[name].items():
            if n.lower() == param.strip().lower():
                return spec
        return None

    def describe(self) -> str:
        if not self.materials:
            return "Materials: (none loaded)"
        lines = ["Materials and their parameters (address a parameter as Material.param):"]
        for name, inputs in self.materials.items():
            lines.append(f"- {name}: " + ", ".join(i.describe() for i in inputs.values()))
        return "\n".join(lines)
