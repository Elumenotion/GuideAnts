#!/usr/bin/env python3
"""Rasterize an SVG to PNG via cairosvg.

usage: rasterize_svg.py SRC.svg OUT.png SCALE ALPHA
  SCALE e.g. 2.0
  ALPHA true|false   (true = transparent PNG for the text layer)

Dimensions come from the SVG root (width/height or viewBox) multiplied by
SCALE. The chrome layer is rasterized opaque; the text layer (background:none
after the split) is rasterized with alpha so ffmpeg can composite it.
"""
from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import cairosvg

NS = "{http://www.w3.org/2000/svg}"


def _base_dimensions(src: Path) -> tuple[float, float]:
    root = ET.parse(src).getroot()
    try:
        width = float((root.get("width") or "").strip())
        height = float((root.get("height") or "").strip())
        return width, height
    except ValueError:
        pass
    vb = (root.get("viewBox") or "").split()
    if len(vb) == 4:
        return float(vb[2]), float(vb[3])
    raise ValueError(f"cannot determine SVG dimensions from {src}")


def main() -> int:
    if len(sys.argv) != 5:
        print("usage: rasterize_svg.py SRC.svg OUT.png SCALE ALPHA", file=sys.stderr)
        print("  SCALE e.g. 2.0    ALPHA true|false", file=sys.stderr)
        return 2
    src, out, scale_s, alpha_s = sys.argv[1:]
    src_path = Path(src)
    if not src_path.is_file():
        print(f"missing source: {src_path}", file=sys.stderr)
        return 1
    scale = float(scale_s)
    alpha = alpha_s.strip().lower() in ("1", "true", "yes")
    width, height = _base_dimensions(src_path)
    out_width = int(round(width * scale))
    out_height = int(round(height * scale))
    cairosvg.svg2png(
        url=str(src_path),
        write_to=out,
        output_width=out_width,
        output_height=out_height,
        background_color="transparent" if alpha else None,
    )
    print(f"{out} {out_width}x{out_height} alpha={alpha} bytes={Path(out).stat().st_size}")
    return 0


def main_like(src: str | Path, out: str | Path, scale: float, alpha: bool) -> str:
    """Programmatic entry (no argv). Returns the output path."""
    src_path = Path(src)
    if not src_path.is_file():
        raise FileNotFoundError(str(src_path))
    width, height = _base_dimensions(src_path)
    out_width = int(round(width * scale))
    out_height = int(round(height * scale))
    cairosvg.svg2png(
        url=str(src_path),
        write_to=str(out),
        output_width=out_width,
        output_height=out_height,
        background_color="transparent" if alpha else None,
    )
    return str(out)


if __name__ == "__main__":
    raise SystemExit(main())
