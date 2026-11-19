"""Canonical-unit conversion for the AI service. Python mirror of src/GseTechPro.Core/Units/UnitConverter.cs.

The unit strings, the linear factors, and the arithmetic order are identical to the C# reference, so a value
converted here round-trips to the same number the app computes (ADR-009 §2, "one canonical mapping, two
implementations"). Do not add a unit or change a factor in one language without the other.

Model: every unit is `display = canonical * scale + offset`. Only temperature (°F) has a non-zero offset.
Pure functions, no I/O.
"""

from __future__ import annotations

from typing import Optional

# --- Canonical unit names (source of truth for storage) ---
PASCAL = "Pa"
CELSIUS = "°C"
FAHRENHEIT = "°F"
METER = "m"
KILOMETER = "km"
MILE = "mi"
FOOT = "ft"
LITER_PER_MINUTE = "L/min"
VOLT = "V"
AMPERE = "A"
HOUR = "h"

# --- Additional display units ---
PSI = "psi"
BAR = "bar"
KILOPASCAL = "kPa"

# --- Exact linear factors (display per canonical) ---
_PSI_PER_PA = 1.0 / 6894.75729316836
_BAR_PER_PA = 1.0 / 100000.0
_KILOPASCAL_PER_PA = 1.0 / 1000.0
_KILOMETER_PER_METER = 1.0 / 1000.0
_MILE_PER_METER = 1.0 / 1609.344
_FOOT_PER_METER = 1.0 / 0.3048
_FAHRENHEIT_PER_CELSIUS = 9.0 / 5.0
_FAHRENHEIT_OFFSET = 32.0

IMPERIAL = "imperial"
METRIC = "metric"
UNIT_SYSTEMS = (IMPERIAL, METRIC)

# ADR-009 §2 table: ReadingType code -> canonical unit (None = no numeric spec).
_READING_TYPE_CANONICAL: dict[str, Optional[str]] = {
    "hydraulic_pressure": PASCAL,
    "fuel_pressure": PASCAL,
    "temperature": CELSIUS,
    "flow": LITER_PER_MINUTE,
    "voltage": VOLT,
    "current": AMPERE,
    "other": None,
}


class UnknownUnitError(ValueError):
    """The unit string is not one of the recognized unit names (C#: ArgumentOutOfRangeException)."""


class UnitDimensionError(ValueError):
    """Two units measure different quantities, so one cannot be converted to the other (C#: ArgumentException)."""


class _UnitDef:
    __slots__ = ("canonical", "scale", "offset")

    def __init__(self, canonical: str, scale: float, offset: float) -> None:
        self.canonical = canonical
        self.scale = scale
        self.offset = offset


_DEFS: dict[str, _UnitDef] = {
    # Pressure (canonical Pa)
    PASCAL: _UnitDef(PASCAL, 1.0, 0.0),
    PSI: _UnitDef(PASCAL, _PSI_PER_PA, 0.0),
    BAR: _UnitDef(PASCAL, _BAR_PER_PA, 0.0),
    KILOPASCAL: _UnitDef(PASCAL, _KILOPASCAL_PER_PA, 0.0),
    # Temperature (canonical °C)
    CELSIUS: _UnitDef(CELSIUS, 1.0, 0.0),
    FAHRENHEIT: _UnitDef(CELSIUS, _FAHRENHEIT_PER_CELSIUS, _FAHRENHEIT_OFFSET),
    # Distance (canonical m)
    METER: _UnitDef(METER, 1.0, 0.0),
    KILOMETER: _UnitDef(METER, _KILOMETER_PER_METER, 0.0),
    MILE: _UnitDef(METER, _MILE_PER_METER, 0.0),
    FOOT: _UnitDef(METER, _FOOT_PER_METER, 0.0),
    # Flow, voltage, current, duration (canonical == display)
    LITER_PER_MINUTE: _UnitDef(LITER_PER_MINUTE, 1.0, 0.0),
    VOLT: _UnitDef(VOLT, 1.0, 0.0),
    AMPERE: _UnitDef(AMPERE, 1.0, 0.0),
    HOUR: _UnitDef(HOUR, 1.0, 0.0),
}


def _get_def(unit: str) -> _UnitDef:
    if unit is None:
        raise UnknownUnitError("unit is required")
    definition = _DEFS.get(unit)
    if definition is None:
        raise UnknownUnitError(f"Unknown unit: {unit!r}")
    return definition


def is_known_unit(unit: Optional[str]) -> bool:
    """True when the unit string is recognized (case-sensitive, exact match like the C# dictionary)."""
    return unit is not None and unit in _DEFS


def canonical_unit(unit: str) -> str:
    """The canonical (SI) unit for a unit string: 'psi' -> 'Pa', '°F' -> '°C'. Raises UnknownUnitError."""
    return _get_def(unit).canonical


def to_canonical(value: float, unit: str) -> float:
    """Convert a value in `unit` to its canonical SI value (C#: ToCanonical)."""
    definition = _get_def(unit)
    return (value - definition.offset) / definition.scale


def from_canonical(value: float, canonical: str, to_unit: str) -> float:
    """Convert a canonical value to `to_unit` (C#: FromCanonical). Raises UnitDimensionError on a mismatch."""
    source = _get_def(canonical).canonical
    target = _get_def(to_unit)
    if source != target.canonical:
        raise UnitDimensionError(
            f"Cannot convert {canonical} ({source}) to {to_unit} ({target.canonical}): different dimensions."
        )
    return value * target.scale + target.offset


def display_unit(canonical: str, system: str) -> str:
    """The default display unit for a canonical unit under the unit system ('imperial' | 'metric')."""
    base = _get_def(canonical).canonical
    if system not in UNIT_SYSTEMS:
        raise ValueError(f"Unknown unit system: {system!r}")
    imperial = system == IMPERIAL
    if base == PASCAL:
        return PSI if imperial else BAR
    if base == CELSIUS:
        return FAHRENHEIT if imperial else CELSIUS
    if base == METER:
        return MILE if imperial else KILOMETER
    return base  # flow, V, A, h (and anything unmapped) stay canonical


def to_display(value: float, canonical: str, system: str) -> tuple[float, str]:
    """Convert a canonical value to its default display unit for the system: returns (value, unit)."""
    unit = display_unit(canonical, system)
    return from_canonical(value, canonical, unit), unit


def canonical_unit_for_reading_type(reading_type: Optional[str]) -> Optional[str]:
    """ADR-009 §2 mapping. `other` (and any unrecognized code) has no numeric canonical unit, so returns None."""
    if reading_type is None:
        return None
    return _READING_TYPE_CANONICAL.get(reading_type)
