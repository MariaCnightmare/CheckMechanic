from __future__ import annotations

import hashlib
import platform
import re
import secrets
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Optional

import cpuinfo
import psutil

CONSENT_VERSION = "2026-02-18"
TOKEN_FILE = Path.home() / ".checkmechanic" / "consent_token"


def _bucket_by_ranges(value: Optional[float], ranges: List[tuple[float, str]]) -> Optional[str]:
    if value is None:
        return None
    for upper, label in ranges:
        if value < upper:
            return label
    return ranges[-1][1]


def _p95(values: List[float]) -> Optional[float]:
    if not values:
        return None
    s = sorted(values)
    idx = max(0, min(len(s) - 1, int(round((len(s) - 1) * 0.95))))
    return float(s[idx])


def get_or_create_consent_token() -> str:
    TOKEN_FILE.parent.mkdir(parents=True, exist_ok=True)
    if TOKEN_FILE.exists():
        raw = TOKEN_FILE.read_text(encoding="utf-8").strip()
        if re.fullmatch(r"[0-9a-fA-F]{64}", raw):
            return raw.lower()
    token = secrets.token_bytes(32).hex()
    TOKEN_FILE.write_text(token, encoding="utf-8")
    return token


def get_token_hash_sha256(token_hex: str) -> str:
    token_bytes = bytes.fromhex(token_hex)
    return hashlib.sha256(token_bytes).hexdigest()


def _cpu_vendor_and_family(cpu_brand: str) -> tuple[Optional[str], Optional[str]]:
    b = cpu_brand.lower()
    vendor = None
    if "intel" in b:
        vendor = "Intel"
    elif "amd" in b:
        vendor = "AMD"
    elif "apple" in b:
        vendor = "Apple"
    elif cpu_brand:
        vendor = "Other"

    family = None
    for candidate in ["Core i9", "Core i7", "Core i5", "Core i3", "Ryzen 9", "Ryzen 7", "Ryzen 5", "Ryzen 3"]:
        if candidate.lower() in b:
            family = candidate
            break
    if family is None and vendor is not None:
        family = "Other"
    return vendor, family


def _cpu_generation_bucket(cpu_brand: str) -> Optional[str]:
    b = cpu_brand.lower()
    nums = re.findall(r"\d{4,5}", b)
    if "intel" in b and nums:
        gen = int(nums[0]) // 1000
        if 12 <= gen <= 14:
            return "12-14"
        if gen <= 11:
            return "11-or-less"
        return "15+"
    if "amd" in b and nums:
        gen = int(nums[0]) // 1000
        if 3 <= gen <= 5:
            return "3-5"
        if gen <= 2:
            return "2-or-less"
        return "6+"
    return None


def _core_count_bucket(core_count: Optional[int]) -> Optional[str]:
    if core_count is None:
        return None
    if core_count <= 4:
        return "1-4"
    if core_count <= 8:
        return "5-8"
    if core_count <= 16:
        return "9-16"
    return "17+"


def _memory_bucket(total_bytes: Optional[int]) -> Optional[str]:
    if total_bytes is None:
        return None
    gb = total_bytes / (1024**3)
    return _bucket_by_ranges(gb, [(8, "<8"), (16, "8-16"), (32, "16-32"), (64, "32-64"), (10**9, "64+")])


def _disk_size_bucket(total_bytes: Optional[int]) -> Optional[str]:
    if total_bytes is None:
        return None
    gb = total_bytes / (1024**3)
    return _bucket_by_ranges(gb, [(256, "<256"), (512, "256-512"), (1024, "512-1024"), (2048, "1024-2048"), (10**9, "2048+")])


def build_telemetry_payload(history: List[Dict[str, Any]], token_hash_sha256: str) -> Dict[str, Any]:
    uname = platform.uname()
    ci = {}
    try:
        ci = cpuinfo.get_cpu_info() or {}
    except Exception:
        ci = {}

    cpu_brand = ci.get("brand_raw") or platform.processor() or ""
    cpu_vendor, cpu_family = _cpu_vendor_and_family(cpu_brand)

    cpu_physical_cores = psutil.cpu_count(logical=False)
    vm = psutil.virtual_memory()
    try:
        disk_usage = psutil.disk_usage("/")
        disk_total = disk_usage.total
    except Exception:
        disk_total = None

    cpu_values = [float(x["cpu_percent"]) for x in history if x.get("cpu_percent") is not None]
    temp_values = [float(x["cpu_temp_c"]) for x in history if x.get("cpu_temp_c") is not None]
    duration_sec = None
    if len(history) >= 2:
        start_ts = history[0].get("ts")
        end_ts = history[-1].get("ts")
        if start_ts is not None and end_ts is not None:
            duration_sec = max(0, int(end_ts - start_ts))

    return {
        "schema_version": "1.0",
        "submitted_at": datetime.now().astimezone().isoformat(timespec="seconds"),
        "consent": {
            "opt_in": True,
            "consent_version": CONSENT_VERSION,
            "data_level": "basic",
            "token_hash_sha256": token_hash_sha256,
        },
        "system": {
            "os": {"family": uname.system or None, "major": int(uname.release.split(".")[0]) if uname.release.split(".")[0].isdigit() else None},
            "cpu": {
                "vendor": cpu_vendor,
                "family": cpu_family,
                "generation_bucket": _cpu_generation_bucket(cpu_brand),
                "core_count_bucket": _core_count_bucket(cpu_physical_cores),
            },
            "gpu": {"vendor": None, "tier_bucket": None, "vram_gb_bucket": None},
            "memory": {"total_gb_bucket": _memory_bucket(getattr(vm, "total", None)), "speed_mhz_bucket": None},
            "motherboard": {"chipset": None, "form_factor": None},
            "storage": {"system_drive_type": None, "system_drive_size_gb_bucket": _disk_size_bucket(disk_total)},
        },
        "bench": {},
        "telemetry_summary": {
            "samples": len(history),
            "load_test": {
                "duration_sec": duration_sec,
                "cpu_util_avg": round(sum(cpu_values) / len(cpu_values), 2) if cpu_values else None,
                "cpu_util_p95": round(_p95(cpu_values), 2) if cpu_values else None,
                "cpu_util_max": round(max(cpu_values), 2) if cpu_values else None,
                "cpu_temp_c_avg": round(sum(temp_values) / len(temp_values), 2) if temp_values else None,
                "cpu_temp_c_p95": round(_p95(temp_values), 2) if temp_values else None,
                "cpu_temp_c_max": round(max(temp_values), 2) if temp_values else None,
            },
        },
    }


def telemetry_preview_lines(payload: Dict[str, Any]) -> List[str]:
    sys = payload.get("system", {})
    telem = payload.get("telemetry_summary", {}).get("load_test", {})
    return [
        f"OS: {sys.get('os', {}).get('family')} {sys.get('os', {}).get('major')}",
        f"CPU分類: {sys.get('cpu', {}).get('vendor')} / {sys.get('cpu', {}).get('family')} / cores={sys.get('cpu', {}).get('core_count_bucket')}",
        f"RAM分類: {sys.get('memory', {}).get('total_gb_bucket')} GB bucket",
        f"ストレージ分類: {sys.get('storage', {}).get('system_drive_size_gb_bucket')} GB bucket",
        f"要約: CPU avg={telem.get('cpu_util_avg')}% p95={telem.get('cpu_util_p95')}% max={telem.get('cpu_util_max')}%",
        f"要約(温度): avg={telem.get('cpu_temp_c_avg')}C p95={telem.get('cpu_temp_c_p95')}C max={telem.get('cpu_temp_c_max')}C",
        "送信しない情報: ホスト名/ユーザー名/IP/MAC/SSID/UUID/シリアル/Windows Product ID/マザーボード型番/BIOSフル文字列",
    ]
