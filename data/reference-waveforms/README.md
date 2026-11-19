# Reference Waveforms (`data/reference-waveforms/`)

> **Important (ADR-011 §3 / §6):** **No real waveform captures exist yet — reference sample data must be supplied from real captures or manuals, never synthesized.**

## Purpose

This directory is the canonical home for known-good reference waveform sample files (`*.wfm`) referenced by `reference_waveforms.data_path` in the database.

- **No synthetic data is ever stored here as "known-good."**
- Simulated signals produced by `SimulatedScopeDevice` (`GseTechPro.Scope`) are strictly developer/demo fixtures (`IsSimulated = true`) and must never be committed or imported into `data/reference-waveforms/`.
- Until real captures from verified equipment or OEM service manuals are collected, `reference_waveforms.data_path` remains `""` (empty string), and numeric expectations in `reference_waveforms.expected_json` must either cite the manual + page or be marked `"is_todo": true` (`TODO: needs spec from manual`).

## `.wfm` Binary Format (ADR-011 §6)

Reference sample files (`reference_waveforms/{reference_waveform_id}.wfm`) and saved work-order captures (`waveforms/{waveform_id}.wfm`) share the exact single-file little-endian binary format implemented by `GseTechPro.Scope.WaveformFile`:

| Offset / Repeat | Type | Field | Description |
|---|---|---|---|
| `0..7` | `byte[8]` | `magic` | ASCII `GSEWAV1\0` (`47 53 45 57 41 56 31 00`) |
| `8..11` | `int32` (LE) | `version` | Format version (`1`) |
| `12..15` | `int32` (LE) | `channelCount` | Number of captured channels (`1..4`) |
| `16..19` | `int32` (LE) | `sampleCount` | Samples per channel |
| `20..27` | `double` (LE) | `sampleRateHz` | Uniform sample rate in Hz (`i / sampleRateHz` = sample time in seconds) |
| `28..35` | `double` (LE) | `timebaseSecondsPerDivision` | Horizontal timebase in seconds/division |
| Repeated `channelCount` times | `int32` (LE) | `channelIndex` | 1-based channel number (`1` = CH1 … `4` = CH4) |
| Following each `channelIndex` | `sampleCount × double` (LE) | `samples` | Contiguous IEEE-754 little-endian `double` values in **probe-corrected volts (V)** |

### Format Rules

1. **Probe-corrected volts only:** Samples in `.wfm` files are instantaneous voltages (V) at the probe tip after applying `ProbeMultiplier` and hardware calibration. Raw ADC counts are never written.
2. **1-based channel indices:** Channel numbers are always 1-based (`1..4`).
3. **Depth cap:** Total samples across all channels (`channelCount × sampleCount`) are capped at `4,000,000` samples (`32 MB` maximum sample payload).
4. **No JSON sidecar:** Metadata (`channel_config_json`, `measurements_json`, `expected_json`, `source`, `citation`) lives in the SQLite `waveforms` / `reference_waveforms` tables.
