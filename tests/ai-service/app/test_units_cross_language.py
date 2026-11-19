"""Cross-language unit-consistency test.

Pins a fixed matrix of conversions to the SAME expected values that both
src/GseTechPro.Core/Units/UnitConverter.cs and ai-service/app/units.py guarantee,
then asserts both implementations produce identical results. This proves the two
implementations are consistent with each other (ADR-009 §2).

The C# side already has xUnit tests in GseTechPro.Tests that assert these same
values; this Python test is the cross-language mirror.
"""

import pytest
from app.units import (
    to_canonical, from_canonical, display_unit, to_display,
    canonical_unit_for_reading_type, PASCAL, CELSIUS, METER, LITER_PER_MINUTE,
    VOLT, AMPERE, HOUR, PSI, BAR, KILOPASCAL, FAHRENHEIT, KILOMETER, MILE, FOOT,
)


# --- Fixed conversion matrix (same values the C# doc-comment guarantees) ---

def test_psi_to_pa_factor():
    """psi→Pa uses 1/6894.75729316836"""
    # 1 psi = 6894.75729316836 Pa (exact)
    assert to_canonical(1.0, PSI) == pytest.approx(6894.75729316836, rel=1e-12)
    # Round-trip: 80 psi → Pa → psi must return 80
    pa = to_canonical(80.0, PSI)
    assert from_canonical(pa, PASCAL, PSI) == pytest.approx(80.0, rel=1e-12)


def test_bar_to_pa_factor():
    """bar→Pa uses 1/100000"""
    # 1 bar = 100000 Pa (exact)
    assert to_canonical(1.0, BAR) == pytest.approx(100000.0, rel=1e-12)
    pa = to_canonical(5.0, BAR)
    assert from_canonical(pa, PASCAL, BAR) == pytest.approx(5.0, rel=1e-12)


def test_kpa_to_pa_factor():
    """kPa→Pa uses 1/1000"""
    # 1 kPa = 1000 Pa (exact)
    assert to_canonical(1.0, KILOPASCAL) == pytest.approx(1000.0, rel=1e-12)


def test_fahrenheit_to_celsius():
    """°F→°C uses offset 32, scale 5/9 inverse"""
    # 212°F = 100°C (boiling point of water)
    assert to_canonical(212.0, FAHRENHEIT) == pytest.approx(100.0, rel=1e-12)
    # 32°F = 0°C (freezing point)
    assert to_canonical(32.0, FAHRENHEIT) == pytest.approx(0.0, rel=1e-12)
    # Round-trip: 100°C → °F → °C must return 100
    f = from_canonical(100.0, CELSIUS, FAHRENHEIT)
    assert to_canonical(f, FAHRENHEIT) == pytest.approx(100.0, rel=1e-12)


def test_distance_factors():
    """mi/m/km/ft→m factors"""
    # 1 mile = 1609.344 m (exact)
    assert to_canonical(1.0, MILE) == pytest.approx(1609.344, rel=1e-12)
    # 1 km = 1000 m (exact)
    assert to_canonical(1.0, KILOMETER) == pytest.approx(1000.0, rel=1e-12)
    # 1 ft = 0.3048 m (exact)
    assert to_canonical(1.0, FOOT) == pytest.approx(0.3048, rel=1e-12)


def test_identity_units():
    """L/min·V·A·h are identity conversions"""
    for unit in [LITER_PER_MINUTE, VOLT, AMPERE, HOUR]:
        assert to_canonical(42.0, unit) == pytest.approx(42.0, rel=1e-12)
        assert from_canonical(42.0, unit, unit) == pytest.approx(42.0, rel=1e-12)


def test_display_unit_imperial():
    """Imperial display units match C# DisplayUnit()"""
    assert display_unit(PASCAL, "imperial") == PSI
    assert display_unit(CELSIUS, "imperial") == FAHRENHEIT
    assert display_unit(METER, "imperial") == MILE


def test_display_unit_metric():
    """Metric display units match C# DisplayUnit()"""
    assert display_unit(PASCAL, "metric") == BAR
    assert display_unit(CELSIUS, "metric") == CELSIUS
    assert display_unit(METER, "metric") == KILOMETER


def test_to_display_imperial_pressure():
    """80 psi round-trip via canonical (C# doc-comment example)"""
    pa = to_canonical(80.0, PSI)
    value, unit = to_display(pa, PASCAL, "imperial")
    assert unit == PSI
    assert value == pytest.approx(80.0, rel=1e-12)


def test_to_display_imperial_temperature():
    """100°C → 212°F (C# doc-comment example)"""
    value, unit = to_display(100.0, CELSIUS, "imperial")
    assert unit == FAHRENHEIT
    assert value == pytest.approx(212.0, rel=1e-12)


def test_canonical_unit_for_reading_type():
    """ADR-009 §2: ReadingType → canonical unit mapping"""
    # Pressure types → Pa
    assert canonical_unit_for_reading_type("hydraulic_pressure") == PASCAL
    assert canonical_unit_for_reading_type("fuel_pressure") == PASCAL
    # Temperature → °C
    assert canonical_unit_for_reading_type("temperature") == CELSIUS
    # Flow → L/min
    assert canonical_unit_for_reading_type("flow") == LITER_PER_MINUTE
    # Voltage → V
    assert canonical_unit_for_reading_type("voltage") == VOLT
    # Current → A
    assert canonical_unit_for_reading_type("current") == AMPERE
    # Other → None (no numeric spec)
    assert canonical_unit_for_reading_type("other") is None
    # Unrecognized → None
    assert canonical_unit_for_reading_type("unknown_type") is None
    # None input → None
    assert canonical_unit_for_reading_type(None) is None
