# DTC / SPN / FMI reference library

Phase 2 seed content for the `dtc_library` table (`docs/ARCHITECTURE.md` §2.2). This is a deliberately bounded first set, not a claim of complete standard or OEM coverage.

**Initial inventory: 540 rows**

| File | Stored `protocol` | Rows | Scope |
|---|---|---:|---|
| `j1939.json` | `J1939` | 214 | Core engine, fuel, air, lubrication, cooling, electrical, calculated-data, vehicle-speed, torque-control and J1939-network SPN/FMI combinations |
| `j1587.json` | `J1708` | 124 | Standard J1587 MID/PID/SID/FMI combinations, centered on MID 128 plus initial transmission and brake examples |
| `obd2.json` | `OBD2` | 202 | Generic, ISO/SAE-controlled `P0xxx` powertrain DTCs for engine, fuel, air, ignition, emissions, speed, electrical and controller faults |

All three data files are **flat JSON arrays**. Each array element represents one possible `dtc_library` row and has exactly the nine source fields below.

## 1. Database mapping

| JSON field | `dtc_library` column | Type / rule |
|---|---|---|
| `protocol` | `protocol` | Exact `ProtocolType` database string: `J1939`, `J1708`, or `OBD2` |
| `code` | `code` | Full display/lookup key. Together with `protocol`, forms the database `UNIQUE (protocol, code)` key |
| `spn` | `spn` | Integer SPN only for J1939; JSON `null` for J1587 and OBD II |
| `fmi` | `fmi` | Integer FMI for J1939/J1587; JSON `null` for OBD II |
| `description` | `description` | Standard parameter/code title plus the cited FMI title where applicable |
| `system` | `system` | GSE Tech Pro search/grouping taxonomy; not a severity assignment or an SAE-defined category |
| `severity` | `severity` | Currently JSON `null` in every row; see §5 |
| `likely_causes` | `likely_causes` | Conservative inspection guidance, not an OEM diagnosis or an assertion that all listed causes exist |
| `source_citation` | `source_citation` | Exact standard revision and appendix/table/identifier supporting the description |

The data layer owns `id`, `created_at`, and `updated_at`. On an initial insert it should generate the UUID and ISO-8601 UTC timestamps; on a content update it should preserve the existing row identity. Apply a transactional **upsert by the exact `(protocol, code)` invariant**; never identify a row by description text alone.

### Canonical code keys

- J1939: `SPN <decimal> FMI <decimal>`, for example `SPN 110 FMI 3`
- J1587: `MID <decimal> PID|SID <decimal> FMI <decimal>`, for example `MID 128 PID 100 FMI 1`
- OBD II: the uppercase five-character generic DTC, for example `P0300`

Do not silently rewrite these strings when matching. Decoder output should canonicalize separators and leading zeros once at the protocol boundary, then perform the lookup. If there is no exact row, retain and display the raw numeric identifiers and report that no library match was found. **Never substitute a nearby code or invent a description.**

## 2. Source and clean-room policy

Descriptions were authored from the named published standards. No diagnostic-tool binaries, OEM proprietary code databases, Bosch/NEXIQ/TOPDON data, or reverse-engineered material were used. Public copies were used only to read the standards' identifier and description tables; repository files contain the derived JSON reference data, not vendor assets.

Every row keeps its own definition-level citation. The public links below are re-check aids, **not substitutes for the standard and section recorded in each row**.

### J1939 definitions

- **SAE J1939-71 MAR2011**, *Vehicle Application Layer (Through May 2010)*, Appendix B, individual SPN definitions. Canonical publication identifier: `J1939/71_201103` (`https://doi.org/10.4271/J1939/71_201103`). [Publicly readable reference copy](https://forge.codesys.com/forge/talk/CODESYS-V2/thread/e3add933b3/dbf2/attachment/SAEJ1939_71.pdf).
- **SAE J1939-73 SEP2006**, *Application Layer — Diagnostics*, Appendix A, individual FMI definitions (including FMI 20/21 and FMI 31). Canonical publication identifier: `J1939/73_200609` (`https://doi.org/10.4271/J1939/73_200609`). [Public UNECE-hosted copy](https://www.unece.org/fileadmin/DAM/trans/doc/2006/wp29/WP29-140-06e.pdf).
- The older **J1939-73 MAR2004** Appendix A was also used to cross-check the unchanged FMI 0–19 text. [Publicly readable reference copy](https://d1.amobbs.com/bbs_upload782111/files_36/ourdev_616616QUMTNX.pdf).

J1939 descriptions are a composition of **two separately cited definitions**: the SPN title and the FMI title. Most SPN titles come from J1939-71 Appendix B; the network definitions SPN 639 and 640 come from J1939-73 §§5.7.13.4–5.7.13.5. This composition is important: the standard does not say every ECU can emit every SPN/FMI pair. A row is a generic decode to use **if that pair is received**, not a declaration that the pair is implemented or valid for a particular engine.

FMI 22–30 are not seeded because they are reserved in the cited revision. Do not assign them meanings without checking the applicable newer standard/OEM source.

### J1587 definitions running on the J1708 link

- **SAE J1587 FEB2002**, *Electronic Data Interchange Between Microcomputer Systems in Heavy-Duty Vehicle Applications*. Canonical publication identifier: `J1587_200202` (`https://doi.org/10.4271/J1587_200202`). [Publicly readable reference copy](https://cdn.hackaday.io/files/18651797964384/SAE%20J1587%20(2002%20Standard).pdf).
- Table 1 supplies the MID title; Table 2 supplies the PID title; Table 6 supplies subsystem identifiers; Table 7 supplies the FMI definition. J1587 §3.8/§3.9 explains their composition as diagnostic information.
- No proprietary PID (`PPID`), proprietary SID (`PSID`), manufacturer-specific blink-code mapping, or OEM-specific repair instruction is implied.

The application's `ProtocolType` value is `J1708` because that is the existing contract for the physical/data-link protocol. The filename and citations correctly identify **J1587 as the application/message format**. Do not rename the stored protocol to `J1587` without a coordinated contract migration.

#### MID is part of the J1587 identity

A J1587 PID/SID cannot safely be interpreted globally without its MID. Therefore:

- `code` carries the full MID + PID/SID + FMI tuple.
- `spn` stays null; a J1587 PID is **not** a J1939 SPN merely because the number is the same.
- The description prefixes the standard MID name (`Engine #1`, `Transmission`, or `Brakes, Power Unit`) to make the source visible to the technician.
- Engine SIDs defined for MIDs `128, 175, 183, 184, 185, 186` are initially seeded for **MID 128 only**. Do not reuse those descriptions under another engine number without preserving the actual MID and checking the intended representation.

Common MID assignments that may be useful to the decoder, all from J1587 FEB2002 Table 1, are listed here as protocol metadata rather than standalone faults: `128 Engine #1`, `129 Turbocharger`, `130 Transmission`, `131 Power Takeoff`, `132 Axle, Power Unit`, `136 Brakes, Power Unit`, `140 Instrument Cluster`, `142 Vehicle Management System`, `143 Fuel System`, `144 Cruise Control`, `145 Road Speed Indicator`, `146 Cab Climate Control`, `150 Suspension, Power Unit`, `158 Electrical Charging System`, `164 Multiplex`, `170 Electrical`, `173 Engine Retarder`, and `174 Cranking/Starting System`. A MID on its own identifies a transmitting module; it is **not by itself a DTC**, so MID-only rows are intentionally not in `dtc_library`.

J1587 FMI 15 is reserved in the cited edition, and the extended J1939-style FMI range must not be applied to legacy J1587 messages.

### OBD II generic P-codes

- **SAE J2012 APR2002**, *Diagnostic Trouble Code Definitions*, Normative Appendix B, Tables B2–B7. Canonical publication identifier: `J2012_200204` (`https://doi.org/10.4271/J2012_200204`). This revision declares itself technically equivalent to **ISO/DIS 15031-6: April 30, 2002**, apart from minor reorganization of §§1–2.
- [Incorporated-by-reference public copy at law.resource.org](https://law.resource.org/pub/us/cfr/ibr/005/sae.j2012.2002.pdf) and [OCR text at Internet Archive](https://archive.org/download/gov.law.sae.j2012.2002/sae.j2012.2002_djvu.txt).
- The code prefix alone must not be used to assert ownership: `P0xxx` entries are ISO/SAE controlled, while other ranges have manufacturer-controlled and/or SAE-reserved portions. This initial file contains only explicitly listed generic `P0xxx` rows, not manufacturer-controlled `P1xxx` codes or an unverified `P2xxx/P3xxx` set.

OBD II diagnostic transport (J1979 / ISO 15031-5) reports the DTC bytes; the J2012/ISO 15031-6 definition supplies the code naming. No SPN or FMI is synthesized for these codes.

## 3. `system`, `likely_causes`, and use in diagnostics

- `system` is a local, consistent taxonomy to support filtering and navigation. It does not pretend SAE assigned equipment severity or vehicle applicability.
- `description` is standards-derived. For J1939/J1587 it places the SPN/PID/SID title before the FMI title. Parenthesized bank/cylinder/MID information is retained in the description.
- `likely_causes` is deliberately labeled inspection guidance, not standard wording. It names broad things to inspect (connections, power/ground/reference, sensor signal, mechanical supply, actuator response, source ECU, network/configuration) based on the code's diagnostic category. It must not be displayed as a confirmed root cause.
- These files contain **no diagnostic thresholds, voltage/pressure/time specifications, torque values, part numbers, or repair procedures**. OEM service information and the exact engine/equipment application take precedence.
- A DTC identifies a suspected area to test, not a part to replace. Before clearing a code, preserve active/pending/stored status, occurrence data, freeze frame, equipment identity, and related symptoms. Clearing is not a repair and does not establish the cause.

For circuit codes, avoid confusing an electrical state with the physical condition. For example, an open low-side sensor/return can produce a high electrical input; circuit-high does not by itself prove overheating or overpressure. Several rows explicitly retain that warning because it prevents incorrect shop conclusions.

## 4. Revision and application limits

The citations deliberately identify the exact revision used instead of claiming that a list from an arbitrary website is “the current standard.” Later J1939DA/J2012DA and OEM service data may rename parameters or assign additional codes/FMIs. The numeric code remains the raw received identity; do not silently overwrite it with a newer or OEM-specific description.

Before using generic causes on an asset, confirm equipment make/model, engine and ECU, calibration, DTC status, and the relevant manual. Where an OEM publishes a different code-specific meaning or procedure, preserve the source distinction and use that OEM procedure rather than forcing this generic decode.

These are reference rows, **not assertions that**:

- all listed SPN/FMI or MID/PID/SID/FMI combinations are implemented by every ECU;
- all code combinations are valid for all GSE;
- the same numeric J1587 PID and J1939 SPN always represent identical semantics across all revisions;
- an active, pending, stored, or emissions-related classification can be inferred from the library row; or
- an MIL/lamp command, derate, shutdown plan, or safe-to-operate decision can be inferred from a DTC title.

## 5. Severity policy

`severity` is `null` throughout this initial set.

Neither SAE J2012 generic DTCs nor J1587 FMIs define a universal shop urgency scale. J1939 FMIs 0/1 and 15–18 include `Most Severe Level`, `Moderately Severe Level`, and `Least Severe Level` **as part of the FMI definition itself**. Those phrases describe the failure-mode range; they are not an OEM-approved universal app severity, equipment shutdown criterion, or aircraft-ramp safety rating.

Therefore the application must not mechanically map those words to `High`, `Medium`, or `Low`. A later OEM-backed content set can add severity for a specific asset/controller using the matching manual and lamp/derate strategy. Until then, present severity as unknown and show the DTC status/lamp information reported by the ECU separately.

## 6. Extending this set

1. Add rows to the appropriate flat array, maintaining numeric DTC/SPN/PID/SID/FMI order where practical.
2. Verify every identifier and description against an allowed published standard or an OEM manual appropriate to the asset. Record the revision and precise table/section/page in `source_citation`.
3. Add separate rows when a different FMI changes the meaning. Do not concatenate unrelated FMIs into one invented description.
4. Keep manufacturer-specific content separate and identify its exact make/model/ECU applicability. Do not import a proprietary database wholesale.
5. Leave unsupported values out rather than writing `unknown` as a pseudo-description. A genuinely needed, unverified placeholder must be conspicuously marked `TODO: verify` with no invented description; it should not be presented as a finished library entry. **This first set intentionally has no placeholder rows.**
6. Keep `severity` null unless a documented OEM source supports it. Preserve system/cause fields as guidance, never as manufacturer specifications.
7. Run structural validation and duplicate-code checks, then have a domain reviewer check representative rows against the cited pages.

Aftertreatment DPF/SCR/DEF, broader transmission/ABS/HVAC, additional J1939 SPN ranges, more J1587 MIDs/SIDs, generic `P2xxx` additions, manufacturer-specific codes, UDS DTCs, OEM blink-code cross-references, and OEM-specific severity/procedures are intentionally left for later cited content work.

## 7. Validation

From the repository root, this PowerShell snippet checks JSON syntax, exact source-field shape, per-file protocol values, code-pattern consistency, uniqueness of the database key, J1587 FMI range, and nonempty citations. The expected counts are also asserted so accidental truncation is detected.

```powershell
@'
import json
import re
from pathlib import Path

root = Path("data/dtc")
expected = {"j1939.json": 214, "j1587.json": 124, "obd2.json": 202}
protocols = {"j1939.json": "J1939", "j1587.json": "J1708", "obd2.json": "OBD2"}
patterns = {
    "J1939": re.compile(r"^SPN ([0-9]+) FMI ([0-9]+)$"),
    "J1708": re.compile(r"^MID ([0-9]+) (PID|SID) ([0-9]+) FMI ([0-9]+)$"),
    "OBD2": re.compile(r"^P[0-3][0-9A-F]{3}$"),
}
fields = {"protocol", "code", "spn", "fmi", "description", "system",
          "severity", "likely_causes", "source_citation"}
seen = set()
for filename, count in expected.items():
    rows = json.loads((root / filename).read_text(encoding="utf-8"))
    assert len(rows) == count, (filename, len(rows), count)
    for row in rows:
        assert set(row) == fields, (filename, row.get("code"), set(row) ^ fields)
        protocol = row["protocol"]
        code = row["code"]
        assert protocol == protocols[filename], (filename, code, protocol)
        assert (protocol, code) not in seen, ("duplicate", protocol, code)
        seen.add((protocol, code))
        assert row["description"] and row["source_citation"], (filename, code)
        match = patterns[protocol].fullmatch(code)
        assert match, (filename, code)
        if protocol == "J1939":
            assert row["spn"] == int(match[1])
            assert row["fmi"] == int(match[2])
            assert 0 <= row["spn"] <= 524287 and 0 <= row["fmi"] <= 31
        elif protocol == "J1708":
            assert row["spn"] is None
            assert row["fmi"] == int(match[4]) and 0 <= row["fmi"] <= 14
        else:
            assert row["spn"] is None and row["fmi"] is None
print(f"PASS: {len(seen)} unique DTC reference rows")
'@ | python -
```

A loader/integration test should additionally upsert all 540 rows into a clean SQLite schema and verify all 540 distinct `(protocol, code)` keys survive a second idempotent import. The domain review should not be skipped merely because JSON validation passes: it is the check for wrong standard wording, revision errors, and unsafe shop guidance.
