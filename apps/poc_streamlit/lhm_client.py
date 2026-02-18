from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Any, Iterable, Optional

import requests

_FLOAT_RE = re.compile(r"(-?\d+(?:\.\d+)?)")


@dataclass(frozen=True)
class LhmLeaf:
    path: str
    value_raw: str
    value_num: Optional[float]
    unit: Optional[str]


def _parse_num_and_unit(value_raw: str) -> tuple[Optional[float], Optional[str]]:
    if not value_raw:
        return None, None
    m = _FLOAT_RE.search(value_raw)
    if not m:
        return None, None
    num = float(m.group(1))
    unit = value_raw[m.end(1) :].strip() or None
    return num, unit


def _walk(node: Any, parents: list[str]) -> Iterable[LhmLeaf]:
    if not isinstance(node, dict):
        return

    text = str(node.get("Text", "")).strip()
    value = str(node.get("Value", "")).strip()

    cur_parents = parents
    if text:
        cur_parents = parents + [text]

    children = node.get("Children")
    if isinstance(children, list) and children:
        for c in children:
            yield from _walk(c, cur_parents)
        return

    if value:
        path = " / ".join(cur_parents) if cur_parents else text or "(unknown)"
        num, unit = _parse_num_and_unit(value)
        yield LhmLeaf(path=path, value_raw=value, value_num=num, unit=unit)


def fetch_lhm_leaves(base_url: str = "http://localhost:8085", timeout: float = 0.8) -> list[LhmLeaf]:
    url = base_url.rstrip("/") + "/data.json"
    r = requests.get(url, timeout=timeout)
    r.raise_for_status()
    data = r.json()

    leaves: list[LhmLeaf] = []
    if isinstance(data, dict):
        if isinstance(data.get("Children"), list):
            for c in data["Children"]:
                leaves.extend(list(_walk(c, [])))
        else:
            leaves.extend(list(_walk(data, [])))
    elif isinstance(data, list):
        for x in data:
            leaves.extend(list(_walk(x, [])))
    return leaves


def pick_temperature(leaves: list[LhmLeaf], include_keywords: list[str]) -> Optional[LhmLeaf]:
    if not leaves:
        return None

    keys = [k.lower() for k in include_keywords if k.strip()]
    for leaf in leaves:
        p = leaf.path.lower()
        v = (leaf.unit or "").lower()

        is_temp_unit = "°c" in v or "c" == v or "celsius" in v
        looks_temp = is_temp_unit or ("temp" in p) or ("temperature" in p) or ("°c" in leaf.value_raw.lower())
        if not looks_temp:
            continue

        if not keys:
            return leaf

        if any(k in p for k in keys):
            return leaf

    return None

