from pathlib import Path

import pytest

from madmapper_agent.materials import Catalog

ROOT = Path(__file__).resolve().parent.parent


def test_all_materials_parse():
    catalog = Catalog.load(ROOT / "materials")
    assert set(catalog.materials) >= {"HandStringVisualizer", "GridSweep", "SurfaceFill", "HoloHands", "ChainHang", "IcarusFall"}
    assert "waveform" not in catalog.materials["HandStringVisualizer"]   # audio is fed by MadMapper


def test_spec_lookup_and_clamping():
    catalog = Catalog.load(ROOT / "materials")
    assert catalog.spec("gridsweep", "PROGRESS").name == "progress"
    assert catalog.spec("HoloHands", "target").clamp([1.5, -0.2]) == [1.0, 0.0]
    assert catalog.spec("ChainHang", "maskFrame").clamp(0) is False
    assert catalog.spec("IcarusFall", "timeOffset").clamp(25) == 20.0
    with pytest.raises(ValueError):
        catalog.spec("GridSweep", "hinge").clamp(7)
