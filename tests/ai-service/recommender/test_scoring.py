"""Pure-function tests for recommender/scoring.py (ADR-010 §3-§5). No database."""

from __future__ import annotations

from datetime import datetime, timezone

import pytest

from recommender.scoring import (
    CandidateSignals,
    QuerySignals,
    cosine,
    format_utc,
    jaccard,
    parse_timestamp,
    rank_score,
    recency_phrase,
    recency_weight,
    render_evidence,
    similarity_breakdown,
    similarity_composite,
    tokenize,
    wilson_lower_bound,
    wo_display_number,
)

NO_QUERY = QuerySignals(frozenset(), frozenset(), frozenset(), frozenset())


# ---- §4 Wilson smoothing (worked numbers from ADR-010 §4)


def test_wilson_one_for_one_is_0_207():
    assert wilson_lower_bound(1, 1) == pytest.approx(0.207, abs=1e-3)


def test_wilson_45_for_50_is_0_786():
    assert wilson_lower_bound(45, 50) == pytest.approx(0.786, abs=1e-3)


def test_wilson_45_for_50_outranks_1_for_1():
    assert wilson_lower_bound(45, 50) > wilson_lower_bound(1, 1)


def test_wilson_no_attempts_is_zero_and_never_shown_as_a_rate():
    assert wilson_lower_bound(0, 0) == 0.0


def test_wilson_zero_successes_is_zero():
    assert wilson_lower_bound(0, 5) == pytest.approx(0.0, abs=1e-12)


def test_wilson_rejects_successes_above_attempts():
    with pytest.raises(ValueError):
        wilson_lower_bound(3, 2)


def test_wilson_grows_with_evidence_at_same_proportion():
    assert wilson_lower_bound(4, 5) < wilson_lower_bound(40, 50) < wilson_lower_bound(400, 500)


# ---- §4 rank blend and recency step function


def test_rank_score_formula():
    assert rank_score(0.5, 1.0, 1.0) == pytest.approx(0.65)


def test_low_sample_cannot_overtake_high_sample_in_rank():
    # ADR worst case: 1-for-1 with max similarity and recency still below 45-for-50 with none of the bonus.
    low = rank_score(wilson_lower_bound(1, 1), 1.0, 1.0)
    high = rank_score(wilson_lower_bound(45, 50), 0.0, 0.0)
    assert high > low


@pytest.mark.parametrize(
    "age, expected",
    [(0, 1.0), (30, 1.0), (31, 0.75), (90, 0.75), (91, 0.5), (365, 0.5), (366, 0.25), (None, 0.25), (-2, 1.0)],
)
def test_recency_step_function(age, expected):
    assert recency_weight(age) == expected


# ---- §3 similarity


def test_jaccard_basic_and_empty_union_guard():
    assert jaccard(frozenset({"a", "b"}), frozenset({"b", "c"})) == pytest.approx(1 / 3)
    assert jaccard(frozenset(), frozenset()) == 0.0


def test_tokenizer_lowercases_splits_non_alnum_drops_stopwords_no_stemming():
    tokens = tokenize("The pump's noise: HIGH_pressure, 2-stage!")
    assert tokens == frozenset({"pump", "s", "noise", "high", "pressure", "2", "stage"})
    assert "the" not in tokens
    assert "pumps" not in tokens  # no stemming: 'pumps' would be a different token


def test_tokenizer_empty_input():
    assert tokenize(None) == frozenset()
    assert tokenize("   ") == frozenset()


def test_composite_uses_all_four_weights():
    query = QuerySignals(
        symptoms=frozenset({"s1", "s2"}),
        dtcs=frozenset({("spn100", "j1939")}),
        systems=frozenset({"hydraulic"}),
        tokens=frozenset({"pump", "noise"}),
    )
    cand = CandidateSignals(
        symptoms=frozenset({"s1"}),  # 1/2 = 0.5
        dtcs=frozenset({("spn100", "j1939")}),  # 1.0
        system="hydraulic",  # 1.0
        tokens=frozenset({"pump"}),  # jaccard 1/2 = 0.5
    )
    # 0.45*0.5 + 0.25*1 + 0.20*1 + 0.10*0.5 = 0.725 (weights already sum to 1)
    assert similarity_composite(query, cand) == pytest.approx(0.725)


def test_composite_renormalizes_when_query_has_no_dtcs():
    query = QuerySignals(
        symptoms=frozenset({"s1", "s2"}),
        dtcs=frozenset(),
        systems=frozenset({"hydraulic"}),
        tokens=frozenset({"pump", "noise"}),
    )
    cand = CandidateSignals(frozenset({"s1"}), frozenset({("x", "y")}), "hydraulic", frozenset({"pump"}))
    # (0.45*0.5 + 0.20*1 + 0.10*0.5) / (0.45 + 0.20 + 0.10) = 0.475 / 0.75 = 0.63333
    assert similarity_composite(query, cand) == pytest.approx(0.475 / 0.75)


def test_composite_is_zero_when_query_has_no_signal():
    cand = CandidateSignals(frozenset({"s1"}), frozenset(), "hydraulic", frozenset({"pump"}))
    assert similarity_composite(NO_QUERY, cand) == 0.0


def test_breakdown_marks_undefined_signals_none():
    query = QuerySignals(frozenset({"s1"}), frozenset(), frozenset(), frozenset())
    cand = CandidateSignals(frozenset(), frozenset({("a", "b")}), None, frozenset())
    parts = similarity_breakdown(query, cand)
    assert parts == {"symptom_sim": 0.0, "dtc_sim": None, "system_sim": None, "text_sim": None}


def test_system_match_requires_candidate_system_present():
    query = QuerySignals(frozenset(), frozenset(), frozenset({"electrical"}), frozenset())
    assert similarity_breakdown(query, CandidateSignals(frozenset(), frozenset(), None, frozenset()))["system_sim"] == 0.0


# ---- §3 optional vector path helper


def test_cosine_identical_orthogonal_and_zero_norm():
    assert cosine([1.0, 2.0], [1.0, 2.0]) == pytest.approx(1.0)
    assert cosine([1.0, 0.0], [0.0, 1.0]) == pytest.approx(0.0)
    assert cosine([0.0, 0.0], [1.0, 0.0]) == 0.0


def test_cosine_dimension_mismatch_is_an_error():
    with pytest.raises(ValueError):
        cosine([1.0], [1.0, 0.0])


# ---- timestamps


def test_parse_seven_digit_fraction_with_z():
    parsed = parse_timestamp("2026-10-27T12:00:00.0000000Z")
    assert parsed == datetime(2026, 10, 27, 12, 0, 0, tzinfo=timezone.utc)


def test_parse_naive_timestamp_is_utc():
    assert parse_timestamp("2026-10-27T12:00:00") == datetime(2026, 10, 27, 12, tzinfo=timezone.utc)


def test_parse_offset_converts_to_utc_and_truncates_fraction():
    parsed = parse_timestamp("2026-10-27T14:00:00.123456789+02:00")
    assert parsed == datetime(2026, 10, 27, 12, 0, 0, 123456, tzinfo=timezone.utc)


def test_parse_garbage_is_none():
    assert parse_timestamp("not a date") is None
    assert parse_timestamp(None) is None
    assert parse_timestamp("") is None


def test_format_utc_matches_wire_shape():
    assert format_utc(datetime(2026, 10, 27, 12, tzinfo=timezone.utc)) == "2026-10-27T12:00:00.0000000Z"


# ---- §5 evidence


def test_canonical_evidence_string():
    text = render_evidence(
        success_count=18,
        attempt_count=20,
        model_label="TUG MA-50",
        average_labor_hours=2.1,
        last_done_days=21,
        wo_number="4471",
    )
    assert text == "Fixed 18 of 20 on TUG MA-50, avg 2.1 hrs, last done 3 weeks ago, WO #4471"


def test_evidence_omits_labor_clause_when_none():
    text = render_evidence(
        success_count=1, attempt_count=1, model_label="TUG MA-50",
        average_labor_hours=None, last_done_days=3, wo_number="12",
    )
    assert text == "Fixed 1 of 1 on TUG MA-50, last done 3 days ago, WO #12"


def test_evidence_omits_recency_clause_when_unknown():
    text = render_evidence(
        success_count=2, attempt_count=3, model_label="TUG MA-50",
        average_labor_hours=1.0, last_done_days=None, wo_number="9",
    )
    assert text == "Fixed 2 of 3 on TUG MA-50, avg 1.0 hrs, WO #9"


def test_evidence_labor_rounds_to_one_decimal():
    text = render_evidence(
        success_count=1, attempt_count=1, model_label="X", average_labor_hours=round(2.04, 1),
        last_done_days=0, wo_number="1",
    )
    assert "avg 2.0 hrs" in text


@pytest.mark.parametrize(
    "days, phrase",
    [
        (0, "today"),
        (1, "yesterday"),
        (2, "2 days ago"),
        (6, "6 days ago"),
        (7, "1 week ago"),
        (13, "1 week ago"),
        (14, "2 weeks ago"),
        (21, "3 weeks ago"),
        (55, "7 weeks ago"),
        (56, "1 month ago"),
        (95, "3 months ago"),
    ],
)
def test_recency_phrasing(days, phrase):
    assert recency_phrase(days) == phrase


def test_recency_phrase_future_timestamp_reads_as_today():
    assert recency_phrase(-3) == "today"


@pytest.mark.parametrize(
    "number, wo_id, expected",
    [
        ("4471", "3f2a9c1e-0000-4000-8000-000000000000", "4471"),
        (" 4471 ", "3f2a9c1e-0000-4000-8000-000000000000", "4471"),
        (None, "3f2a9c1e-0000-4000-8000-000000000000", "3f2a9c1e"),
        ("   ", "3f2a9c1e-0000-4000-8000-000000000000", "3f2a9c1e"),
    ],
)
def test_wo_display_number_prefers_number_then_id_prefix(number, wo_id, expected):
    assert wo_display_number(number, wo_id) == expected
