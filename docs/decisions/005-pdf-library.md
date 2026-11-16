# ADR-005 — PDF library: avoid PyMuPDF (AGPL); use permissive stack

- **Status:** Accepted (pending user approval)
- **Date:** 2026-11-15
- **Owner:** architect

## Context

Manual ingestion must parse PDFs (and convert `.doc/.docx/.ppt/.pptx` offline to PDF) and OCR scanned pages, all bundled in a proprietary, offline, shipping product. PyMuPDF is the most convenient library but is **AGPL-3.0** (or a paid Artifex commercial license).

## Decision

**Do not use PyMuPDF under AGPL.** Build the ingestion/OCR path on permissive libraries:
- Text/table extraction: `pypdf` (BSD-3-Clause), `pdfplumber` (MIT), and/or `pdfminer.six` (MIT); `pypdfium2` (BSD-3-Clause/Apache-2.0) for rendering/thumbnails.
- OCR: **Tesseract** (Apache-2.0), or OS-provided `Windows.Media.Ocr`.
- Office→PDF conversion: bundled LibreOffice headless (MPL-2.0) when needed; otherwise skip when a PDF version exists.

## Alternatives considered

- **PyMuPDF under AGPL:** copyleft/network clauses would force disclosure of our proprietary source; not acceptable for a closed shipping app.
- **PyMuPDF commercial license:** viable but adds per-copy cost and a procurement cycle; revisit only if permissive libraries prove insufficient.

## Consequences

- No copyleft exposure from the PDF stack; everything bundles cleanly.
- Slightly more integration work (multiple libs) for tables/diagrams vs PyMuPDF's all-in-one API.
- OCR text is deterministic (Tesseract), consistent with the rule that exact values come from code/OCR, not the model.
