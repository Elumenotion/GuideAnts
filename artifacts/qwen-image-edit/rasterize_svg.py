"""Rasterize an SVG to PNG via PyMuPDF."""

from __future__ import annotations

import sys
from pathlib import Path

import pymupdf


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
    alpha = alpha_s.lower() in ("1", "true", "yes")
    doc = pymupdf.open(src_path)
    try:
        pix = doc[0].get_pixmap(matrix=pymupdf.Matrix(scale, scale), alpha=alpha)
        pix.save(out)
        print(f"{out} {pix.width}x{pix.height} alpha={alpha} bytes={Path(out).stat().st_size}")
    finally:
        doc.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
