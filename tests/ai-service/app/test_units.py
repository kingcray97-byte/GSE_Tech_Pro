"""app/units.py: exact mirror of src/GseTechPro.Core/Units/UnitConverter.cs and the ADR-009 §2 canonical mapping."""

from __future__ import annotations

import math

import pytest

from ai_support import REPO  # noqa: F401  (puts ai-service/ on sys.path)
from app import units

PSI_PER_PA = 1.0 / 6894.75729316836


def _close(a: float, b: float, rel: float = 1e-12) -> bool:
    return math.isclose(a, b, rel_tol=rel, abs_tol=1e-12)


def test_unit_strings_match_the_csharp_constants_exactly():
    # Same strings as UnitConverter.cs (case and degree sign included): a different string breaks cross-checks.
    assert units.PASCAL == "Pa" and units.CELSIUS == "°C" and units.FAHRENHEIT == "°F"
    assert units.METER == "m" and units.KILOMETER == "km" and units.MILE == "mi" and units.FOOT == "ft"
    assert units.LITER_PER_MINUTE == "L/min" and units.VOLT == "V" and units.AMPERE == "A" and units.HOUR == "h"
    assert units.PSI == "psi" and units.BAR == "bar" and units.KILOPASCAL == "kPa"


def test_factors_are_the_csharp_factors():
    assert units._PSI_PER_PA == PSI_PER_PA
    assert units._BAR_PER_PA == 1.0 / 100000.0
    assert units._KILOPASCAL_PER_PA == 1.0 / 1000.0
    assert units._KILOMETER_PER_METER == 1.0 / 1000.0
    assert units._MILE_PER_METER == 1.0 / 1609.344
    assert units._FOOT_PER_METER == 1.0 / 0.3048
    assert units._FAHRENHEIT_PER_CELSIUS == 9.0 / 5.0
    assert units._FAHRENHEIT_OFFSET == 32.0


@pytest.mark.parametrize("unit", ["psi", "bar", "kPa", "Pa", "°C", "°F", "m", "km", "mi", "ft", "L/min", "V", "A", "h"])
def test_known_units_are_recognized(unit):
    assert units.is_known_unit(unit) is True


@pytest.mark.parametrize("unit", ["PSI", "Psi", "celsius", "", " psi", "furlong", "°c", None])
def test_unknown_or_miscased_units_are_rejected(unit):
    assert units.is_known_unit(unit) is False


def test_canonical_unit_of_each_display_unit():
    assert units.canonical_unit("psi") == "Pa"
    assert units.canonical_unit("bar") == "Pa"
    assert units.canonical_unit("kPa") == "Pa"
    assert units.canonical_unit("°F") == "°C"
    assert units.canonical_unit("mi") == "m"
    assert units.canonical_unit("km") == "m"
    assert units.canonical_unit("ft") == "m"
    assert units.canonical_unit("L/min") == "L/min"


def test_unknown_unit_raises_the_unknown_unit_error_which_is_a_value_error():
    with pytest.raises(units.UnknownUnitError):
        units.to_canonical(1.0, "furlong")
    assert issubclass(units.UnknownUnitError, ValueError)


def test_psi_to_pascal_matches_the_factor():
    assert _close(units.to_canonical(1500.0, "psi"), 1500.0 / PSI_PER_PA)
    assert _close(units.to_canonical(1500.0, "psi"), 10342135.939752540, rel=1e-9)


def test_psi_round_trips_exactly_enough():
    for value in (0.0, 1.0, 80.0, 1000.0, 1500.0, 2300.5):
        canonical = units.to_canonical(value, "psi")
        assert _close(units.from_canonical(canonical, "Pa", "psi"), value, rel=1e-12)
        pascal = value * 6894.75729316836
        assert _close(units.to_canonical(units.from_canonical(pascal, "Pa", "psi"), "psi"), pascal, rel=1e-12)


def test_bar_and_kpa_factors():
    assert _close(units.to_canonical(1.0, "bar"), 100000.0)
    assert _close(units.to_canonical(7.0, "kPa"), 7000.0)
    assert _close(units.from_canonical(250000.0, "Pa", "bar"), 2.5)


def test_fahrenheit_offset_round_trip_and_fixed_points():
    assert _close(units.to_canonical(212.0, "°F"), 100.0)
    assert _close(units.to_canonical(32.0, "°F"), 0.0)
    assert _close(units.from_canonical(100.0, "°C", "°F"), 212.0)
    assert _close(units.to_canonical(units.from_canonical(-40.0, "°C", "°F"), "°F"), -40.0)


def test_distance_factors_round_trip():
    assert _close(units.from_canonical(1609.344, "m", "mi"), 1.0)
    assert _close(units.from_canonical(1000.0, "m", "km"), 1.0)
    assert _close(units.to_canonical(1.0, "ft"), 0.3048)


def test_identity_units_do_not_change():
    for unit in ("L/min", "V", "A", "h"):
        assert units.to_canonical(12.5, unit) == 12.5
        assert units.from_canonical(12.5, unit, unit) == 12.5


def test_from_canonical_rejects_a_dimension_mismatch():
    with pytest.raises(units.UnitDimensionError):
        units.from_canonical(1.0, "Pa", "V")
    assert issubclass(units.UnitDimensionError, ValueError)


def test_display_units_for_each_system():
    assert units.display_unit("Pa", "imperial") == "psi"
    assert units.display_unit("Pa", "metric") == "bar"
    assert units.display_unit("°C", "imperial") == "°F"
    assert units.display_unit("°C", "metric") == "°C"
    assert units.display_unit("m", "imperial") == "mi"
    assert units.display_unit("m", "metric") == "km"
    assert units.display_unit("V", "imperial") == "V"
    assert units.display_unit("L/min", "metric") == "L/min"
    with pytest.raises(ValueError):
        units.display_unit("Pa", "martian")


def test_to_display_matches_the_csharp_examples():
    # UnitConverter.cs doc: ToDisplay(ToCanonical(80, Psi), Pascal, Imperial) == (80, "psi")
    value, unit = units.to_display(units.to_canonical(80.0, "psi"), "Pa", "imperial")
    assert unit == "psi" and _close(value, 80.0)
    # ToDisplay(ToCanonical(100, Celsius), Celsius, Imperial) == (212, "°F")
    value, unit = units.to_display(units.to_canonical(100.0, "°C"), "°C", "imperial")
    assert unit == "°F" and _close(value, 212.0)


@pytest.mark.parametrize(
    ("reading_type", "expected"),
    [
        ("hydraulic_pressure", "Pa"),
        ("fuel_pressure", "Pa"),
        ("temperature", "°C"),
        ("flow", "L/min"),
        ("voltage", "V"),
        ("current", "A"),
        ("other", None),
    ],
)
def test_adr009_reading_type_to_canonical_mapping(reading_type, expected):
    # docs/decisions/009-gse-troubleshooting.md §2 table, identical in C#.
    assert units.canonical_unit_for_reading_type(reading_type) == expected


def test_reading_type_mapping_is_none_for_unknown_or_missing_codes():
    assert units.canonical_unit_for_reading_type(None) is None
    assert units.canonical_unit_for_reading_type("hydraulics") is None
