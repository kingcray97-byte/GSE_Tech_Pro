"""OCR for scanned (image-only) manual pages: pypdfium2 renders the page, Windows.Media.Ocr reads it.

Same engine as the app's data-plate reader (PLAN 7.3): OS provided, offline, nothing to license or bundle beyond the
Python bindings (pywinrt, MIT) and pdfium (pypdfium2, BSD-3/Apache-2.0, ADR-005). OCR text is deterministic but can
misread characters (0/O, 1/I, 5/S): every chunk made from it is tagged `source:ocr` so answers tell the technician to
check numbers against the page. The model never reads the page.

`make_page_ocr()` returns None (with the reason) when the engine cannot run, for example off Windows, without the
bindings, or with no OCR language installed; ingest then reports the scanned pages as needing OCR, as before.
"""

from __future__ import annotations

import asyncio
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterable, Iterator, Protocol

RENDER_DPI = 300  # Windows OCR reads 8-10 pt manual text reliably at 300 dpi (checked on the 660-28 scans)
MAX_SIDE_PX = 6000  # a large drawing sheet is rendered smaller rather than at full dpi
MAX_PIXELS = 30_000_000  # ~120 MB BGRA: one page at a time, released before the next (8 GB tablet)


@dataclass(frozen=True)
class OcrPage:
    number: int  # 1-based page number
    lines: list[str]
    error: str | None = None  # this page could not be rendered or read


class PageOcr(Protocol):
    engine: str

    def read_pages(
        self, pdf_path: Path, page_numbers: Iterable[int], keep_going: Callable[[], bool] = lambda: True
    ) -> Iterator[OcrPage]:
        """Yield one OcrPage per requested page, in order. Stops early when keep_going() is False."""
        ...


def render_scale(width_pt: float, height_pt: float) -> float:
    """pdfium scale (pixels per point) for RENDER_DPI, reduced so neither side exceeds MAX_SIDE_PX and the page
    stays under MAX_PIXELS."""
    scale = RENDER_DPI / 72.0
    long_side = max(width_pt, height_pt, 1.0)
    scale = min(scale, MAX_SIDE_PX / long_side)
    area = max(width_pt * height_pt, 1.0)
    if area * scale * scale > MAX_PIXELS:
        scale = (MAX_PIXELS / area) ** 0.5
    return scale


class WindowsPdfOcr:
    """Windows.Media.Ocr over pages rendered by pdfium. Use make_page_ocr() to build one."""

    def __init__(self, engine_obj, language_tag: str, max_dimension: int) -> None:
        self._engine = engine_obj
        self._max_dimension = max_dimension
        self.engine = f"Windows.Media.Ocr ({language_tag})"

    def read_pages(
        self, pdf_path: Path, page_numbers: Iterable[int], keep_going: Callable[[], bool] = lambda: True
    ) -> Iterator[OcrPage]:
        import pypdfium2 as pdfium

        pdf = pdfium.PdfDocument(str(pdf_path))
        try:
            for number in page_numbers:
                if not keep_going():
                    return
                try:
                    lines = self._read_one(pdf, number, pdfium)
                    yield OcrPage(number=number, lines=lines)
                except Exception as exc:  # noqa: BLE001 - one bad page never stops the manual
                    yield OcrPage(number=number, lines=[], error=f"{type(exc).__name__}: {exc}"[:200])
        finally:
            pdf.close()

    def _read_one(self, pdf, number: int, pdfium) -> list[str]:
        from winrt.windows.graphics.imaging import BitmapPixelFormat, SoftwareBitmap
        from winrt.windows.storage.streams import Buffer

        page = pdf[number - 1]
        bitmap = None
        try:
            width_pt, height_pt = page.get_size()
            scale = render_scale(width_pt, height_pt)
            if self._max_dimension > 0:
                scale = min(scale, self._max_dimension / max(width_pt, height_pt, 1.0))
            bitmap = page.render(scale=scale, force_bitmap_format=pdfium.raw.FPDFBitmap_BGRA, fill_color=(255, 255, 255, 255))
            pixels = bitmap.to_numpy()
            height, width = pixels.shape[:2]
            data = pixels.tobytes()
            del pixels
            buffer = Buffer(len(data))
            buffer.length = len(data)
            memoryview(buffer)[:] = data
            del data
            software_bitmap = SoftwareBitmap.create_copy_from_buffer(buffer, BitmapPixelFormat.BGRA8, width, height)
            result = asyncio.run(self._recognize(software_bitmap))
            return [line.text for line in result.lines]
        finally:
            if bitmap is not None:
                bitmap.close()
            page.close()

    async def _recognize(self, software_bitmap):
        return await self._engine.recognize_async(software_bitmap)


def make_page_ocr() -> tuple[PageOcr | None, str | None]:
    """(ocr, None) when Windows OCR can run here, else (None, reason)."""
    try:
        import pypdfium2  # noqa: F401
    except ImportError:
        return None, "PDF renderer (pypdfium2) is not installed"
    try:
        from winrt.windows.globalization import Language
        from winrt.windows.media.ocr import OcrEngine
    except ImportError:
        return None, "Windows OCR bindings (winrt-Windows.Media.Ocr) are not installed"
    except Exception as exc:  # noqa: BLE001 - not Windows, or WinRT failed to start
        return None, f"Windows OCR is unavailable ({type(exc).__name__})"
    try:
        engine = OcrEngine.try_create_from_user_profile_languages()
        if engine is None:
            engine = OcrEngine.try_create_from_language(Language("en-US"))
        if engine is None:
            return None, "no Windows OCR language is installed (Settings > Time & language > Language)"
        tag = engine.recognizer_language.language_tag
        max_dimension = int(OcrEngine.max_image_dimension)
    except Exception as exc:  # noqa: BLE001
        return None, f"Windows OCR is unavailable ({type(exc).__name__}: {exc})"[:200]
    return WindowsPdfOcr(engine, tag, max_dimension), None
