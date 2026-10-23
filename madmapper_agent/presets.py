"""Named looks for a shader material (presets.json) and the shader's input ranges."""

from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from pathlib import Path


class PresetError(KeyError):
    def __str__(self) -> str:
        return str(self.args[0])


@dataclass
class ShaderInput:
    name: str
    type: str
    min: float = 0.0
    max: float = 1.0
    values: list[int] = field(default_factory=list)  # for "long" (menu) inputs

    def normalize(self, value: float) -> float:
        """Map a value in the shader's range to 0-1."""
        if self.type == "long" and self.values:
            if len(self.values) == 1:
                return 0.0
            return self.values.index(int(value)) / (len(self.values) - 1)
        span = self.max - self.min
        return 0.0 if span == 0 else (float(value) - self.min) / span

    def clamp(self, value: float) -> float | int:
        if self.type == "long":
            v = int(round(value))
            if self.values and v not in self.values:
                raise ValueError(f"'{self.name}' must be one of {self.values}")
            return v
        return max(self.min, min(self.max, float(value)))


def read_isf_inputs(path: str | Path) -> dict[str, ShaderInput]:
    """Parse the JSON header of an ISF shader and return its controllable inputs."""
    text = Path(path).read_text(encoding="utf-8")
    m = re.search(r"/\*(.*?)\*/", text, re.S)
    if not m:
        raise ValueError(f"{path} has no ISF JSON header")
    inputs = {}
    for i in json.loads(m.group(1)).get("INPUTS", []):
        if i["TYPE"] in ("float", "long"):
            inputs[i["NAME"]] = ShaderInput(
                name=i["NAME"],
                type=i["TYPE"],
                min=float(i.get("MIN", 0.0)),
                max=float(i.get("MAX", 1.0)),
                values=list(i.get("VALUES", [])),
            )
    return inputs


@dataclass
class Preset:
    name: str
    description: str
    values: dict[str, float]


@dataclass
class PresetLibrary:
    presets: dict[str, Preset] = field(default_factory=dict)
    inputs: dict[str, ShaderInput] = field(default_factory=dict)

    @classmethod
    def load(cls, path: str | Path) -> "PresetLibrary":
        path = Path(path)
        data = json.loads(path.read_text(encoding="utf-8"))
        presets = {
            name.strip().lower(): Preset(name, p.get("description", ""), p["values"])
            for name, p in data.get("presets", {}).items()
            if not name.startswith("_")
        }
        inputs = {}
        if data.get("material"):
            inputs = read_isf_inputs(path.parent / data["material"])
        return cls(presets, inputs)

    def get(self, name: str) -> Preset:
        key = name.strip().lower()
        if key not in self.presets:
            known = ", ".join(sorted(self.presets)) or "none"
            raise PresetError(f"Unknown preset '{name}'. Known presets: {known}")
        return self.presets[key]

    def input(self, name: str) -> ShaderInput | None:
        """Look up a shader input case-insensitively."""
        for n, i in self.inputs.items():
            if n.lower() == name.strip().lower():
                return i
        return None

    def describe(self) -> str:
        if not self.presets:
            return "Presets: (none configured)"
        lines = ["Presets:"] + [f"- {p.name}: {p.description}" for p in self.presets.values()]
        if self.inputs:
            params = ", ".join(
                f"{i.name} ({i.values[0]}-{i.values[-1]})" if i.type == "long" and i.values
                else f"{i.name} ({i.min:g}-{i.max:g})"
                for i in self.inputs.values()
            )
            lines.append(f"Shader parameters: {params}")
        return "\n".join(lines)
