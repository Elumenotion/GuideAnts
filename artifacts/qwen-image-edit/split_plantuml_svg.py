"""Split a PlantUML SVG into chrome-only and text-only layers (stdlib only)."""

from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NS = "http://www.w3.org/2000/svg"
ET.register_namespace("", NS)
ET.register_namespace("xlink", "http://www.w3.org/1999/xlink")


def _local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _is_text(el: ET.Element) -> bool:
    return _local(el.tag) == "text"


def _is_graphic(el: ET.Element) -> bool:
    return _local(el.tag) in {
        "path",
        "rect",
        "ellipse",
        "circle",
        "line",
        "polyline",
        "polygon",
        "image",
        "use",
    }


def _strip(root: ET.Element, predicate) -> None:
    for parent in list(root.iter()):
        for child in list(parent):
            if predicate(child):
                parent.remove(child)


def split_svg(src: Path, chrome_path: Path, text_path: Path) -> None:
    chrome_tree = ET.parse(src)
    text_tree = ET.parse(src)
    _strip(chrome_tree.getroot(), _is_text)
    _strip(text_tree.getroot(), _is_graphic)
    text_root = text_tree.getroot()
    style = text_root.get("style") or ""
    parts = [p.strip() for p in style.split(";") if p.strip() and not p.strip().lower().startswith("background")]
    parts.append("background:none")
    text_root.set("style", ";".join(parts))
    chrome_tree.write(chrome_path, encoding="utf-8", xml_declaration=True)
    text_tree.write(text_path, encoding="utf-8", xml_declaration=True)


def main() -> int:
    if len(sys.argv) != 4:
        print("usage: split_plantuml_svg.py SRC chrome.svg text.svg", file=sys.stderr)
        return 2
    src, chrome, text = map(Path, sys.argv[1:])
    if not src.is_file():
        print(f"missing source SVG: {src}", file=sys.stderr)
        return 1
    split_svg(src, chrome, text)
    print(f"chrome={chrome} text={text}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
