"""Named looks for one shader material (presets.json)."""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path


class PresetError(KeyError):
    def __str__(self) -> str:
        return str(self.args[0])


@dataclass
class Preset:
    name: str
    description: str
    values: dict[str, float]


@dataclass
class PresetLibrary:
    presets: dict[str, Preset] = field(default_factory=dict)
    material: str | None = None   # material file stem, e.g. "HandStringVisualizer"

    @classmethod
    def load(cls, path: str | Path) -> "PresetLibrary":
        data = json.loads(Path(path).read_text(encoding="utf-8"))
        presets = {
            name.strip().lower(): Preset(name, p.get("description", ""), p["values"])
            for name, p in data.get("presets", {}).items()
            if not name.startswith("_")
        }
        material = Path(data["material"]).stem if data.get("material") else None
        return cls(presets, material)

    def get(self, name: str) -> Preset:
        key = name.strip().lower()
        if key not in self.presets:
            known = ", ".join(sorted(self.presets)) or "none"
            raise PresetError(f"Unknown preset '{name}'. Known presets: {known}")
        return self.presets[key]

    def describe(self) -> str:
        if not self.presets:
            return "Presets: (none configured)"
        lines = [f"Presets for {self.material}:"]
        lines += [f"- {p.name}: {p.description}" for p in self.presets.values()]
        return "\n".join(lines)
