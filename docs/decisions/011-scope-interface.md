# ADR-011 — Oscilloscope: process boundary, capture contract, measurement math, reference comparison, and persistence

- **Status:** Accepted
- **Date:** 2026-11-17
- **Owner:** architect

## Context

Phase 6 adds the oscilloscope (PicoScope 7 Automotive workflow) behind the single
`IScopeDevice` interface that has been a Phase-1 placeholder. `ARCHITECTURE.md` §1.1 left an
open question: "Hantek's 6074BE SDK DLLs are often 32-bit" and suggested Hantek might route
through the 32-bit device host. Three Phase-1 unknowns are now resolved (verified by lead):

1. **The Hantek 6074BE SDK is dual-architecture.** `Reference/HT6074BE_SDK/Dll/{x64,x86}/*.dll`
   and `Lib/{x64,x86}/*.lib` both exist (HTHardDll, HTSoftDll, HTDisplayDll, MeasDll), and the
   driver ships `Hantek6000BAMD64.Sys`. So Hantek capture can run **x64 in-process**, exactly like
   PicoScope — there is no 32-bit DLL that must stay out of the 64-bit app.
2. **PicoSDK is not in `Reference/`** (only the PicoScope 7 installer). The PicoScope wrapper will
   P/Invoke against the *published PicoSDK API* but will **not be exercised against hardware** in
   Phase 6 — the same clean-room "compile against the published API, defer hardware verification"
   pattern already used for J2534/RP1210.
3. **No real waveform captures exist** in the repo. Reference waveforms must be manual-cited or
   `TODO: needs spec from manual`; nothing is ever synthesized as "known-good."

This ADR is the normative contract that `scope` (wrappers + math engine), `database` (rows + file
paths), `gse-domain-expert` (guided tests + reference `expected_json`), and `ui-winui` (scope
screen) all build against. It deliberately stops at the contract layer: no device wrappers, no UI,
no SQL migrations are written here.

## Decision

### 1. Process boundary — x64 in-process, no new 32-bit host

- Both brands load **in-process** inside `GseTechPro.Scope` (a new x64 .NET 8 class library,
  referenced by `GseTechPro.App`). **No 32-bit process and no host32 routing for scope.** This
  resolves the §1.1 open question in favor of "in-process" for both brands.
- `GseTechPro.Scope` is the *only* place vendor scope DLLs are ever loaded:
  `PicoScopeDevice` P/Invokes PicoSDK; `Hantek6074BeDevice` P/Invokes `HTHardDll`/`HTSoftDll`
  (`dsoHTSearchDevice` → `dsoHTDeviceConnect` → `dsoHTSetCHAndTrigger`/`dsoHTSetSampleRate` →
  `dsoHTStartCollectData`/`dsoHTStartTrigger` → `dsoHTGetData` → `dsoHTReadCalibrationData`, per
  the published `HeadFiles_UTF8/HTHardDll.h` — block capture + raw per-channel WORD data is
  confirmed exposed). The app never references vendor namespaces; it only sees `IScopeDevice`.
- **PicoSDK loading note:** the wrapper compiles against the published PicoSDK API (clean-room, from
  Pico's public header/signatures) but is **not exercised against hardware** in Phase 6; the
  simulator is the DoD path. Hardware verification is a later, hardware-in-hand task.
- **Licensing/driver notes (unchanged from §7.1):** PicoSDK is proprietary-permissive — bundle with
  the copyright notice intact. Hantek SDK license is **unverified** (no public license text found)
  and the Hantek 6074BE driver is **not digitally signed** (Win10/11 install friction) — treat as
  user-installed until Hantek confirms; do not bundle.
- The `0x03` frame-kind byte in the host32 framing (ADR-007) stays **reserved but unused** — it
  costs nothing to keep the tag space stable and it documents that scope streaming never traverses
  the 32-bit boundary. No host32 command is added.

### 2. Capture mode — block (triggered) capture only, for now

- Phase 6 implements **block (triggered) capture**: arm trigger → acquire `sampleCount` samples per
  enabled channel → stop → read the buffer. This covers all six automotive guided tests (crank
  sensor, injector, alternator ripple, starter current, CAN signal quality, solenoid/valve current)
  and the simulator.
- **Streaming / long-memory capture is explicitly deferred** (not required for Phase 6). Hantek
  exposes it via `dsoHTGetScanData`/`dsoSDGetData`/roll mode; PicoSDK exposes streaming mode — both
  are out of scope now. The `IScopeDevice` shape is chosen so streaming can be added later without
  breaking the block path (a future `StartStreamingAsync` + push event, mirroring
  `IDiagnosticAdapter.LiveDataReceived`).

### 3. `IScopeDevice` final shape

```csharp
public interface IScopeDevice
{
    string Name { get; }                 // display name
    ScopeDeviceKind Kind { get; }        // picoscope | hantek | simulator
    bool IsSimulated => false;           // default member; simulator overrides to true
    bool IsConnected { get; }
    int ChannelCount { get; }            // analog channels (1-based indexing, see below)

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();              // idempotent
    Task ConfigureChannelAsync(int channel, ScopeChannelConfig config, CancellationToken cancellationToken = default);
    Task StartCaptureAsync(ScopeCaptureRequest request, CancellationToken cancellationToken = default);
    Task StopCaptureAsync(CancellationToken cancellationToken = default);
    Task<ScopeCapture> ReadCaptureAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScopeMeasurement>> ComputeMeasurementsAsync(int channel, CancellationToken cancellationToken = default);
}
```

- **Discovery** lives on a separate factory (`IScopeDeviceFactory`, new) so the simulator/reardware
  switch mirrors the diagnostic adapter pattern:
  `DiscoverAsync() → IReadOnlyList<ScopeDeviceInfo>`, then
  `OpenAsync(ScopeDeviceInfo) → IScopeDevice`. `DiscoverAsync` **always includes the simulator
  entry** so the app is usable with no hardware. `ScopeDeviceInfo` carries `Id` (stable per-device
  identifier: USB serial / bus location / `"sim-1"`), `Name`, `Kind`, `ChannelCount`, `Serial?`,
  and `IsSimulated`.
- **Guarantees the interface makes** (the math/UI rely on these; implementations must uphold them):
  - **Samples are volts, already probe-corrected.** `ScopeCapture.Channels[ch]` is a `double[]` of
    instantaneous voltages (V) at the probe tip — the wrapper applies `ProbeMultiplier` (and the
    Hantek calibration readback) before returning. No raw ADC counts ever leave the wrapper.
  - **Uniform sample rate** `ScopeCapture.SampleRateHz` (Hz, ≥ 1). Sample times are
    `i / SampleRateHz`, `i = 0…SampleCount-1`.
  - **1-based channel numbering** everywhere (CH1…CH4, matching hardware silkscreen and Pico/Hantek
    APIs). `ScopeChannelConfig.Channel`, `ConfigureChannelAsync(int channel)`,
    `ScopeCapture.Channels` keys, `ComputeMeasurementsAsync(int channel)`, and
    `ScopeMeasurement.Channel` all use 1-based indices.
  - **Thread-safety / UI:** every method returns `Task` and must never block the UI thread; capture
    runs on a worker. `ReadCaptureAsync` returns the completed buffer; it may be called once per
    arm (implementations may return the same buffer or throw `InvalidOperationException` if none is
    pending — the contract is "call `StartCaptureAsync`, await completion, then `ReadCaptureAsync`").
    No events are required in the block path (the UI polls completion via the `Task`s).
  - **Provenance:** `IsSimulated` (device-level) and `ScopeCapture.IsSimulated` /
    `ScopeMeasurement.IsSimulated` (per-value) carry simulator-vs-real end-to-end, so a simulated
    capture saved into a work order is never mistaken for real (ADR-007 provenance rule).

### 4. Waveform measurement contract (the six measurements)

`MeasurementKind` (new enum): `Frequency`, `Peak`, `PeakToPeak`, `DutyCycle`, `RiseTime`,
`Ripple`, `Current`. `ComputeMeasurementsAsync(channel)` returns one `ScopeMeasurement` per kind
that is *definable* for that channel (see per-kind rules). Each result carries `Kind`, `Channel`,
`Value?` (null when not computed), `Unit`, `Status` (`Computed | NoTrigger | TooFewSamples |
OutOfRange | FlatSignal | NotConfigured`), an optional human `Detail`, and provenance `IsSimulated`.

Given samples `v[0..N-1]` in volts, uniform rate `fs` Hz, channel clamp scale `S` (A/V):

| Kind | Unit | Exact definition | Not-computed status |
|---|---|---|---|
| **Peak (Vp)** | V | `max(v)` (highest instantaneous voltage) | `TooFewSamples` if `N < 2` |
| **Peak-to-peak (Vpp)** | V | `max(v) − min(v)` | `TooFewSamples` if `N < 2` |
| **Frequency** | Hz | `f = (K−1) / Σ Tₖ`, where `tₖ` are the rising crossings of the mid level `vMid = (min+max)/2` (linearly interpolated) and `Tₖ = tₖ₊₁ − tₖ` (mean period). | `FlatSignal` if fewer than 2 rising crossings (no measurable cycle) |
| **Duty cycle** | % | `100 × (time above vMid) / period`, averaged over all complete periods (rising→falling above-threshold dwell / full period) | `FlatSignal` if no complete cycle |
| **Rise time** | s | `t(90% of Vpp) − t(10% of Vpp)` on a rising edge, levels `vLo+0.10·(vHi−vLo)` / `vLo+0.90·(vHi−vLo)`, linearly interpolated; use the first clean rising edge | `FlatSignal` if the edge never reaches 90% |
| **Ripple** | V | **AC RMS** of the AC component: `√( Σ(vᵢ − mean(v))² / N )` | `TooFewSamples` if `N < 2` |
| **Current** | A | `max(v) × S` (peak current), requires the channel's clamp scale `S = ScopeChannelConfig.CurrentScaleAmpsPerVolt > 0` | `NotConfigured` if `S ≤ 0` |

Justifications recorded so they aren't re-litigated:

- **Peak = `max(v)` and Vpp = `max − min`** are the standard scope readings and are unambiguous for
  signals riding on a DC offset (alternator, sensor, ground).
- **Ripple = AC RMS (mean-subtracted), not Vpp.** A single glitch dominates Vpp and overstates
  steady ripple; AC RMS is the conventional way alternator-ripple specs are quoted (e.g. "≤ 0.5 V
  AC") and is robust to noise. `gse-domain-expert` must author ripple expectations with the same
  AC-RMS semantics.
- **Current = peak current (`max(v)×S`).** The guided current tests (starter inrush, injector,
  solenoid) are pulsed, so "current draw" = peak. `gse-domain-expert` must author current
  expectations with the same peak semantics; if a future test needs mean current, add a new kind
  rather than overload this one.
- **Out-of-range:** the wrapper clamps/`OutOfRange` when the requested `VoltageRangeVolts` is too
  small for the signal or the device reports an overrange — the exact numeric value is then
  untrustworthy and must not be compared to a reference.
- **No-trigger:** block capture returns what it acquired; if no trigger fired within the
  implementation's timeout, the wrapper marks the capture (or the measurement) `NoTrigger` rather
  than inventing a value.

### 5. Reference comparison contract

`reference_waveforms.expected_json` is a JSON document matching `ReferenceExpectations`:

```json
{
  "measurements": [
    { "kind": "frequency",     "channel": 1, "nominal": 120.0, "tolerance": 3.0,  "unit": "Hz" },
    { "kind": "peak_to_peak",  "channel": 1, "min": 11.0, "max": 15.0,            "unit": "V"  },
    { "kind": "ripple",        "channel": 1, "nominal": 0.25, "tolerance": 0.05,   "unit": "V"  },
    { "kind": "current",       "channel": 2, "is_todo": true }
  ]
}
```

- Each `MeasurementExpectation` is **one of**: `{ nominal, tolerance }` (tolerance = **absolute ±
  in the measurement's unit**, symmetric) **or** `{ min, max }` (inclusive). `unit` must match the
  measurement's canonical unit (Hz, V, %, s, A) or the verdict is `na` (unit mismatch). `is_todo`
  marks a placeholder authored before the real spec is known — verdict is `na` with reason
  "reference TODO — needs spec from manual".
- **Verdicts** use `ScopeVerdict` (`pass | fail | na`) per measurement, and record the tolerance
  actually applied (`expected_nominal`/`expected_tolerance` or `expected_min`/`expected_max`).
  Semantics: `na` when (a) the reference has no expectation for that kind, (b) the expectation is
  `is_todo`, (c) the measurement itself is not `Computed`, or (d) the unit mismatches; otherwise
  `pass`/`fail` by the range. Overall verdict: `fail` if any `fail`; else `pass` if ≥ 1 `pass`;
  else `na`.
- **Comparison is deterministic C#**, not the model. The math engine in `GseTechPro.Scope`
  computes `ScopeMeasurement[]` from the capture (no verdicts), then a pure
  `CompareMeasurements(measurements, ReferenceExpectations) → ScopeComparisonResult` fills in the
  verdicts. The AI **only explains** the result — it never judges the raw image or the numbers
  (PROMPT.md "Local AI" safety rules). Comparison is deliberately **not** on `IScopeDevice` so the
  simulator and both hardware wrappers can't diverge, and the math is testable with no hardware.
- `ScopeComparisonResult` is the aggregate the UI renders and the data layer persists:
  `ReferenceWaveformId`, the `Measurements` (with verdicts filled in), and `Overall`.

### 6. Waveform persistence contract

- **Metadata stays in the DB row; samples go to a binary file.** `waveforms.channel_config_json`
  (serialized `ScopeChannelConfig[]`), `timebase`, `sample_rate`, `sample_count`, and
  `measurements_json` (serialized `ScopeMeasurement[]`, *including* the comparison verdicts — one
  column, additive, no schema rewrite) are the human/queryable side. `waveforms.data_path`
  points at the binary samples.
- **File format** — a single little-endian `.wfm` file, per-channel contiguous `double[]` (volts),
  with a fixed binary header so it's cheap to read and needs no JSON sidecar:
  - `byte[8]` magic `GSEWAV1\0` (ASCII); `int32` version `= 1`; `int32` channelCount;
    `int32` sampleCount (per channel); `double` sampleRateHz; `double` timebaseSecondsPerDivision;
    then, repeated per channel: `int32` channelIndex (1-based) followed by
    `sampleCount × double` (little-endian, probe-corrected volts).
  - No per-sample timestamps (uniform rate is in the header). No compression — block captures are
    small and the CPU budget is tight.
- **Path convention** (relative to the app data dir, same as `attachments`/`manuals`):
  `waveforms/{waveform_id}.wfm` and `reference_waveforms/{reference_waveform_id}.wfm`. The
  `.wfm` writer/reader lives in `GseTechPro.Scope` (not Core); the format is specified here so
  `database`, `scope`, and `tests` agree without sharing code.
- **Memory bound.** `double` samples = 8 B each. A block capture of `sampleRate × timebase × 10
  divisions` per channel is small (1 MS/s × 1 ms/div × 10 = 10k samples = 80 KB/channel). The
  scope screen caps total capture depth so **4 channels × depth ≤ 4 M samples = 32 MB** in the
  worst case (a configurable default); the file is written from the capture buffers, and larger
  captures are streamed to disk rather than held fully in memory. This is well inside the 8 GB
  tablet budget, and capture runs off the UI thread.

### 7. Guided-test flow contract

- A `guided_tests` row (name, procedure, `safety_warning_ids`, `reference_waveform_id`, and the
  new `scope_setup_json` = serialized `ScopeCaptureRequest`: channels, timebase, sample rate,
  trigger) is the *recipe*. Its `reference_waveforms` row supplies the *expected outcome*
  (`expected_json`, §5) and, optionally, a *known-good reference sample* (`data_path`, §6) to
  overlay on the screen.
- **Drive:** the scope screen loads the test → shows safety warnings **first** → applies
  `scope_setup_json` to the device (channels + timebase + trigger) → arms and captures → computes
  measurements per channel → overlays the reference sample (if present) → compares against
  `expected_json` (if present) and shows per-measurement pass/fail/na with the tolerance used.
- **Save:** the completed capture is written as `waveforms` (work order FK, `scope_device`,
  `channel_config_json`, `timebase`/`sample_rate`/`sample_count`, `data_path`, `guided_test_id`,
  `reference_waveform_id`, and `measurements_json` = measurements + verdicts). The AI explains the
  saved result (citing the reference's `citation` or `TODO: needs spec from manual`) but the
  numbers and verdicts come from the deterministic math.

## Alternatives considered

- **Route Hantek through the 32-bit host32 process** (the old §1.1 idea): rejected — the Hantek SDK
  is confirmed dual-architecture with x64 DLLs and an x64 driver, so a second process adds a
  boundary, a frame protocol, and failure modes for zero benefit.
- **A dedicated 64-bit scope host process** (scope as a child process with its own IPC): rejected —
  in-process P/Invoke is simpler, has no serialization/pipe latency for bulk sample data, and both
  vendor SDKs are already x64. (If a future vendor ships a crashing/misbehaving scope DLL, we can
  isolate it behind a process later — but that's not needed now.)
- **Streaming-first capture API**: rejected — block capture covers all Phase-6 tests and the
  simulator; streaming is deferred (§2).
- **Put comparison on `IScopeDevice`**: rejected — comparison is pure math over measurements +
  expectations; keeping it in the scope math engine means one implementation shared by simulator
  and both wrappers, and it's testable without hardware. (§5)
- **JSON sidecar for the sample file**: rejected — the DB row already holds the metadata, and a
  fixed binary header parses with a few `BinaryReader` calls (cheaper than a JSON parse on the
  tablet). (§6)

## Consequences

- `GseTechPro.Scope` (new project, owned by `scope`) implements `IScopeDevice` (simulator first,
  then PicoSDK and Hantek P/Invoke), the measurement math, the comparison function, and the `.wfm`
  reader/writer — all against the types in `GseTechPro.Core`.
- `database` adds the additive `guided_tests.scope_setup_json` column and persists
  `measurements_json` as `ScopeMeasurement[]` (measurements + verdicts); `reference_waveforms.expected_json`
  as `ReferenceExpectations`; file paths per §6.
- `gse-domain-expert` authors guided tests + reference `expected_json` with the §4/§5 definitions
  (AC-RMS ripple, peak current) and cites manuals or `TODO: needs spec from manual`; never
  synthesizes known-good data.
- `ui-winui` builds the scope screen against `IScopeDevice`/`IScopeDeviceFactory`,
  `ScopeDeviceInfo`, `ScopeCaptureRequest`, `ScopeMeasurement`, and `ScopeComparisonResult`.
- PicoSDK is compiled against but **not exercised against hardware**; Hantek license + unsigned
  driver remain **unverified** (§1). Both are recorded risks, not Phase-6 blockers (simulator-first).
- Referenced from `docs/ARCHITECTURE.md` §1.1, §1.1.1, §2.2, §8, and the decision-records list.
