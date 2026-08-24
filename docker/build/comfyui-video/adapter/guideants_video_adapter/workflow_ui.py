"""Convert ComfyUI API prompt graphs into browser-loadable UI workflow JSON."""

from __future__ import annotations

from typing import Any

UI_WORKFLOW_VERSION = 0.4
DEFAULT_NODE_WIDTH = 315
DEFAULT_NODE_HEIGHT = 82
GRID_X = 360
GRID_Y = 280
GRID_COLUMNS = 4


def is_api_prompt(value: Any) -> bool:
    if not isinstance(value, dict) or not value:
        return False
    if "nodes" in value or "links" in value:
        return False
    return all(isinstance(node, dict) and "class_type" in node for node in value.values())


def _input_schema(object_info: dict[str, Any], class_type: str) -> tuple[list[str], dict[str, Any]]:
    node_info = object_info.get(class_type, {})
    inputs = node_info.get("input", {})
    required = inputs.get("required", {})
    optional = inputs.get("optional", {})
    ordered_names = list(required.keys()) + list(optional.keys())
    specs: dict[str, Any] = {}
    specs.update(required)
    specs.update(optional)
    return ordered_names, specs


def _input_type_name(spec: Any) -> str:
    if isinstance(spec, list) and spec:
        first = spec[0]
        if isinstance(first, str):
            return first
    return "*"


def _is_link(value: Any) -> bool:
    return (
        isinstance(value, list)
        and len(value) == 2
        and isinstance(value[0], str)
        and value[0].isdigit()
        and isinstance(value[1], int)
    )


def _widget_values(
    ordered_names: list[str],
    specs: dict[str, Any],
    api_inputs: dict[str, Any],
) -> list[Any]:
    widgets: list[Any] = []
    for name in ordered_names:
        if name not in api_inputs:
            continue
        value = api_inputs[name]
        if _is_link(value):
            continue
        if name not in specs:
            widgets.append(value)
            continue
        type_name = _input_type_name(specs[name])
        if type_name in {"MODEL", "CLIP", "VAE", "CONDITIONING", "LATENT", "IMAGE", "MASK"}:
            continue
        widgets.append(value)
    return widgets


def _output_slots(class_type: str, object_info: dict[str, Any]) -> list[dict[str, Any]]:
    node_info = object_info.get(class_type, {})
    outputs = node_info.get("output", [])
    output_names = node_info.get("output_name", outputs)
    slots: list[dict[str, Any]] = []
    for index, output_type in enumerate(outputs):
        name = output_names[index] if index < len(output_names) else str(index)
        slots.append(
            {
                "name": name,
                "type": output_type,
                "links": None,
                "slot_index": index,
            }
        )
    return slots


def api_prompt_to_ui_workflow(
    api_prompt: dict[str, Any],
    object_info: dict[str, Any],
) -> dict[str, Any]:
    """Build a LiteGraph workflow ComfyUI's Workflows browser can open."""
    if not is_api_prompt(api_prompt):
        raise ValueError("expected ComfyUI API prompt format")

    sorted_ids = sorted(int(node_id) for node_id in api_prompt)
    nodes: list[dict[str, Any]] = []
    links: list[list[Any]] = []
    link_id = 1
    outgoing: dict[tuple[int, int], list[int]] = {}

    for order, node_id in enumerate(sorted_ids):
        node_key = str(node_id)
        api_node = api_prompt[node_key]
        class_type = api_node["class_type"]
        api_inputs = api_node.get("inputs", {})
        ordered_names, specs = _input_schema(object_info, class_type)
        if not ordered_names:
            ordered_names = list(api_inputs.keys())

        input_slots: list[dict[str, Any]] = []
        for slot_index, input_name in enumerate(ordered_names):
            input_type = _input_type_name(specs.get(input_name, ["*"]))
            link_ref: int | None = None
            if input_name in api_inputs and _is_link(api_inputs[input_name]):
                source_id = int(api_inputs[input_name][0])
                source_slot = int(api_inputs[input_name][1])
                link_ref = link_id
                links.append(
                    [link_id, source_id, source_slot, node_id, slot_index, input_type]
                )
                outgoing.setdefault((source_id, source_slot), []).append(link_id)
                link_id += 1
            input_slots.append(
                {
                    "name": input_name,
                    "type": input_type,
                    "link": link_ref,
                }
            )

        outputs = _output_slots(class_type, object_info)
        for slot_index, output in enumerate(outputs):
            output_links = outgoing.get((node_id, slot_index))
            if output_links:
                output["links"] = output_links

        col = order % GRID_COLUMNS
        row = order // GRID_COLUMNS
        nodes.append(
            {
                "id": node_id,
                "type": class_type,
                "pos": [col * GRID_X, row * GRID_Y],
                "size": [DEFAULT_NODE_WIDTH, DEFAULT_NODE_HEIGHT],
                "flags": {},
                "order": order,
                "mode": 0,
                "inputs": input_slots,
                "outputs": outputs,
                "properties": {"Node name for S&R": class_type},
                "widgets_values": _widget_values(ordered_names, specs, api_inputs),
            }
        )

    return {
        "last_node_id": max(sorted_ids) if sorted_ids else 0,
        "last_link_id": link_id - 1 if link_id > 1 else 0,
        "nodes": nodes,
        "links": links,
        "groups": [],
        "config": {},
        "extra": {},
        "version": UI_WORKFLOW_VERSION,
    }
