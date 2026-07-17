"""Per-alias vision token presets for router child instances."""

from __future__ import annotations

import re

from guideants_hf.router_mmproj import preset_disables_mmproj

VISION_TOKEN_PRESET_KEYS = frozenset({"image-min-tokens", "image-max-tokens"})

# Gemma 4 mmproj caps image_max_pixels at 645120; 645120 / 2304 = 280 tokens.
GEMMA_IMAGE_MIN_TOKENS = "280"
GEMMA_IMAGE_MAX_TOKENS = "280"

# Qwen-VL grounding accuracy needs a higher floor than Gemma supports.
QWEN_IMAGE_MIN_TOKENS = "1024"


def strip_vision_token_extras(extras: dict[str, str]) -> None:
    for key in list(extras.keys()):
        if key.strip().lower() in VISION_TOKEN_PRESET_KEYS:
            del extras[key]


def apply_alias_vision_token_preset(alias: str, preset: dict[str, str]) -> dict[str, str]:
    """Strip vision token keys and re-apply family-specific values for *alias*."""
    normalized = {
        key: value
        for key, value in preset.items()
        if key.strip().lower() not in VISION_TOKEN_PRESET_KEYS
    }
    if preset_disables_mmproj(normalized):
        return normalized
    alias_lower = alias.strip().lower()
    if re.search(r"qwen", alias_lower):
        normalized["image-min-tokens"] = QWEN_IMAGE_MIN_TOKENS
    elif re.search(r"gemma", alias_lower):
        normalized["image-min-tokens"] = GEMMA_IMAGE_MIN_TOKENS
        normalized["image-max-tokens"] = GEMMA_IMAGE_MAX_TOKENS
    return normalized


def normalize_router_ini_text(text: str) -> str:
    """Rewrite router-models.ini with per-alias vision token normalization."""
    lines = text.splitlines()
    output: list[str] = []
    current_alias: str | None = None
    section_lines: list[str] = []

    def flush_section() -> None:
        nonlocal section_lines, current_alias
        if current_alias is None:
            return

        extras: dict[str, str] = {}
        cleaned: list[str] = []
        for raw_line in section_lines:
            stripped = raw_line.strip()
            if stripped.startswith("#") or stripped.startswith(";"):
                cleaned.append(raw_line)
                continue
            if "=" in stripped:
                key, value = stripped.split("=", 1)
                key_raw = key.strip()
                key_lower = key_raw.lower()
                if key_lower in VISION_TOKEN_PRESET_KEYS:
                    continue
                if key_lower not in {"model", "mmproj", "version"}:
                    extras[key_raw] = value.strip()
                cleaned.append(raw_line)
                continue
            cleaned.append(raw_line)

        normalized_extras = apply_alias_vision_token_preset(current_alias, extras)
        if preset_disables_mmproj(extras):
            normalized_extras = {
                key: value
                for key, value in normalized_extras.items()
                if key.strip().lower() not in VISION_TOKEN_PRESET_KEYS
            }
        vision_keys = {
            key.lower()
            for key in normalized_extras
            if key.lower() in VISION_TOKEN_PRESET_KEYS
        }
        if vision_keys:
            cleaned = [
                line
                for line in cleaned
                if not (
                    "=" in line.strip()
                    and line.strip().split("=", 1)[0].strip().lower() in vision_keys
                )
            ]
            insert_at = 1 if cleaned and cleaned[0].strip().startswith("[") else 0
            for key in sorted(normalized_extras.keys()):
                if key.lower() in VISION_TOKEN_PRESET_KEYS:
                    cleaned.insert(insert_at, f"{key} = {normalized_extras[key]}")
                    insert_at += 1

        output.extend(cleaned)
        section_lines = []
        current_alias = None

    for raw_line in lines:
        stripped = raw_line.strip()
        if stripped.startswith("[") and stripped.endswith("]"):
            flush_section()
            current_alias = stripped[1:-1].strip()
            section_lines = [raw_line]
            continue

        if current_alias is None:
            output.append(raw_line)
            continue

        section_lines.append(raw_line)

    flush_section()

    if not output:
        return ""
    return "\n".join(output) + "\n"
