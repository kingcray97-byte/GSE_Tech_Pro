# Vehicle catalog seed data

These four files are the schema-v1 starter catalog for `IVehicleCatalog`:

| File | `region` value |
|---|---|
| `domestic.json` | `domestic` |
| `asian.json` | `asian` |
| `european.json` | `european` |
| `heavy_duty.json` | `heavy_duty` |

The region strings match the `EnumMember` values on `VehicleRegion`. This is catalog data only: it contains no diagnostic specifications, pressures, torque values, or procedures.

## Schema

```json
{
  "schemaVersion": 1,
  "region": "domestic",
  "years": [2025, 2024, 2023],
  "makes": [
    {
      "name": "Ford",
      "years": [2025, 2024, 2023],
      "models": ["F-150", "Explorer"],
      "notes": "Optional; used for non-standard GSE entries"
    }
  ]
}
```

- `schemaVersion` is the integer schema version, currently `1`.
- `region` is required and must match the file's region code above.
- Top-level `years` is the newest-first union of the non-empty make-year arrays in that file. The starter light-duty and truck coverage is 2000 through 2025, except where a make began later.
- `makes[].name` is the display and lookup name.
- `makes[].years` lists, newest first and without duplicates, the years in which that make is represented by this seed. Arrays are explicit and inclusive; there are no compact range values.
- `makes[].models` contains real, publicly documented model-line names. It is a deliberately small starter set, not an exhaustive manufacturer catalog.
- `makes[].notes` is optional. GSE entries use it to identify equipment that does not follow normal passenger-vehicle year/make/model conventions.

## Model-year limitation

Schema v1 stores **years per make, not years per model**. A year filter can establish whether a make is available, but the model array does not assert that every listed model was offered in every one of those years. For example, a discontinued model can remain in a make's starter model list while that make continues to the present. Exact model-year coverage needs a later schema extension rather than inferred years.

Regional membership is a selector partition, not a VIN-decoding authority. Some requested nameplates intentionally occur in more than one file (for example Toyota in `domestic` and `asian`, and Volvo in `european` and `heavy_duty`). Do not deduplicate them across regions.

## Heavy-duty brands and GSE equipment

`heavy_duty.json` also has a top-level `heavy_duty_brands` array. It lists the nine starter truck brands and the included GSE OEM brands. It is supplementary catalog metadata, not a replacement for `makes`.

The GSE makes (`TUG Technologies`, `TLD`, `Eagle Tugs`, and `Harlan Global Manufacturing`) have an empty `years` array because no production-year range is asserted for their equipment in this seed. They can be returned by an unfiltered make query, but a year-filtered query will not return them. A selector that exposes these entries therefore needs an explicit unknown-year or unfiltered-equipment path; it must not invent a model year to make the normal three-selection flow pass.
