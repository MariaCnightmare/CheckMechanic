from __future__ import annotations

import json
import platform
import time
from datetime import datetime
from typing import Any, Dict, List, Optional, Tuple

import cpuinfo
import psutil
import streamlit as st
from streamlit_autorefresh import st_autorefresh

from consent import build_telemetry_payload, get_or_create_consent_token, get_token_hash_sha256, telemetry_preview_lines
from lhm_client import fetch_lhm_leaves, pick_temperature


def bytes_to_human(n: float) -> str:
    units = ["B", "KB", "MB", "GB", "TB"]
    x = float(n)
    for u in units:
        if x < 1024.0:
            return f"{x:.1f} {u}"
        x /= 1024.0
    return f"{x:.1f} PB"


def get_static_system_info() -> Dict[str, Any]:
    ci = {}
    try:
        ci = cpuinfo.get_cpu_info() or {}
    except Exception:
        ci = {}

    uname = platform.uname()
    return {
        "os": f"{uname.system} {uname.release} ({uname.version})",
        "machine": uname.machine,
        "node": uname.node,
        "cpu_brand": ci.get("brand_raw") or platform.processor() or "(unknown)",
        "cpu_logical_cores": psutil.cpu_count(logical=True),
        "cpu_physical_cores": psutil.cpu_count(logical=False),
        "boot_time": datetime.fromtimestamp(psutil.boot_time()).isoformat(timespec="seconds"),
        "python": platform.python_version(),
    }


def sample_psutil(prev: Optional[Dict[str, Any]]) -> Dict[str, Any]:
    ts = time.time()

    cpu_percent = psutil.cpu_percent(interval=None)
    cpu_percpu = psutil.cpu_percent(interval=None, percpu=True)
    cpu_freq = psutil.cpu_freq()
    vm = psutil.virtual_memory()

    disk = psutil.disk_io_counters()
    net = psutil.net_io_counters()

    disk_read_bps = None
    disk_write_bps = None
    net_sent_bps = None
    net_recv_bps = None

    if prev is not None:
        dt = max(ts - prev["ts"], 1e-6)
        disk_read_bps = (disk.read_bytes - prev["disk_read_bytes"]) / dt
        disk_write_bps = (disk.write_bytes - prev["disk_write_bytes"]) / dt
        net_sent_bps = (net.bytes_sent - prev["net_sent_bytes"]) / dt
        net_recv_bps = (net.bytes_recv - prev["net_recv_bytes"]) / dt

    return {
        "ts": ts,
        "time": datetime.fromtimestamp(ts).isoformat(timespec="seconds"),
        "cpu_percent": cpu_percent,
        "cpu_percpu_percent": cpu_percpu,
        "cpu_freq_mhz": getattr(cpu_freq, "current", None),
        "mem_used": vm.used,
        "mem_total": vm.total,
        "mem_percent": vm.percent,
        "disk_read_bytes": disk.read_bytes,
        "disk_write_bytes": disk.write_bytes,
        "disk_read_bps": disk_read_bps,
        "disk_write_bps": disk_write_bps,
        "net_sent_bytes": net.bytes_sent,
        "net_recv_bytes": net.bytes_recv,
        "net_sent_bps": net_sent_bps,
        "net_recv_bps": net_recv_bps,
    }


def sample_temperature(
    enable_lhm: bool,
    lhm_base_url: str,
    temp_keywords: list[str],
) -> Tuple[Optional[float], Optional[str], Optional[str], List[Tuple[str, str]]]:
    notices: List[Tuple[str, str]] = []
    try:
        temps = psutil.sensors_temperatures(fahrenheit=False)
        if temps:
            for k in ["coretemp", "k10temp", "cpu-thermal", "acpitz"]:
                if k in temps and temps[k]:
                    best = None
                    for t in temps[k]:
                        label = (t.label or "").lower()
                        if "package" in label or "physical" in label:
                            best = t
                            break
                        best = best or t
                    if best is not None:
                        return float(best.current), f"{k}:{best.label or 'temp'}", "psutil", notices
    except Exception as exc:
        notices.append(("warning", f"温度センサー取得(psutil)に失敗: {type(exc).__name__}: {exc}"))

    if enable_lhm:
        try:
            leaves = fetch_lhm_leaves(base_url=lhm_base_url)
            leaf = pick_temperature(leaves, include_keywords=temp_keywords)
            if leaf and leaf.value_num is not None:
                return float(leaf.value_num), leaf.path, "LibreHardwareMonitor(/data.json)", notices
            notices.append(("warning", "LHM data.json は取得できましたが、一致する温度センサーが見つかりませんでした。"))
        except Exception as exc:
            notices.append(("error", f"LHM data.json 取得に失敗: {type(exc).__name__}: {exc}"))

    return None, None, None, notices


st.set_page_config(page_title="CheckMechanic PoC", layout="wide")
st.title("CheckMechanic PoC")
st.caption("CPU/メモリ/IO/ネットワーク＋温度（可能なら）を可視化します。センサーは時々、気分で嘘をつきます。")

with st.sidebar:
    st.header("設定")
    refresh_ms = st.slider("更新間隔 (ms)", 500, 5000, 1000, 100)
    history_len = st.slider("履歴保持（サンプル数）", 60, 3600, 600, 60)

    enable_lhm = st.checkbox("Windows温度: LibreHardwareMonitor を使う", value=True)
    lhm_base_url = st.text_input("LHM Base URL", value="http://localhost:8085")
    temp_keywords_raw = st.text_input(
        "温度センサー探索キーワード（カンマ区切り）",
        value="CPU Package,Core,GPU",
        help="LHMの data.json のパスに含まれる語でヒットさせます（例: CPU Package）。",
    )
    temp_keywords = [x.strip() for x in temp_keywords_raw.split(",") if x.strip()]

    st.divider()
    st.subheader("データ送信同意")
    opt_in = st.checkbox("匿名化・カテゴリ化データの送信に同意する（opt-in）", value=False)
    st.caption("既定はOFFです。同意ON時のみ送信用テレメトリJSONを生成できます。")

    st.divider()
    if st.button("スナップショットを初期化（履歴クリア）"):
        st.session_state.pop("history", None)
        st.session_state.pop("prev", None)
        st.rerun()

st_autorefresh(interval=refresh_ms, key="checkmechanic_autorefresh")

if "sysinfo" not in st.session_state:
    st.session_state["sysinfo"] = get_static_system_info()

if "history" not in st.session_state:
    st.session_state["history"] = []

prev = st.session_state.get("prev")
cur = sample_psutil(prev)
st.session_state["prev"] = cur

temp_c, temp_label, temp_src, temp_notices = sample_temperature(enable_lhm, lhm_base_url, temp_keywords)
cur["cpu_temp_c"] = temp_c
cur["cpu_temp_label"] = temp_label
cur["cpu_temp_source"] = temp_src

st.session_state["history"].append(cur)
if len(st.session_state["history"]) > history_len:
    st.session_state["history"] = st.session_state["history"][-history_len:]

sysinfo = st.session_state["sysinfo"]

kpi1, kpi2, kpi3, kpi4, kpi5 = st.columns(5)
kpi1.metric("CPU %", f"{cur['cpu_percent']:.1f}%")
kpi2.metric("CPU MHz", f"{cur['cpu_freq_mhz']:.0f}" if cur["cpu_freq_mhz"] else "n/a")
kpi3.metric("Mem %", f"{cur['mem_percent']:.1f}%")
kpi4.metric("Disk R/W", f"{bytes_to_human(cur['disk_read_bps'] or 0)}/s | {bytes_to_human(cur['disk_write_bps'] or 0)}/s")
kpi5.metric("Net ↓/↑", f"{bytes_to_human(cur['net_recv_bps'] or 0)}/s | {bytes_to_human(cur['net_sent_bps'] or 0)}/s")

if temp_c is not None:
    st.success(f"温度: {temp_c:.1f} °C  （{temp_label} / {temp_src}）")
else:
    st.warning("温度: 取得できません（WindowsならLibreHardwareMonitorの Remote Web Server をONにして /data.json を確認）")
for level, message in temp_notices:
    if level == "error":
        st.error(message)
    else:
        st.warning(message)

st.subheader("システム情報")
st.json(sysinfo)

hist = st.session_state["history"]
cpu_series = [{"time": x["time"], "cpu_percent": x["cpu_percent"]} for x in hist]
mem_series = [{"time": x["time"], "mem_percent": x["mem_percent"]} for x in hist]
disk_series = [{"time": x["time"], "read_bps": x["disk_read_bps"] or 0, "write_bps": x["disk_write_bps"] or 0} for x in hist]
net_series = [{"time": x["time"], "recv_bps": x["net_recv_bps"] or 0, "sent_bps": x["net_sent_bps"] or 0} for x in hist]
temp_series = [{"time": x["time"], "cpu_temp_c": x["cpu_temp_c"] if x["cpu_temp_c"] is not None else None} for x in hist]

c1, c2 = st.columns(2)
with c1:
    st.subheader("CPU 使用率")
    st.line_chart(cpu_series, x="time", y="cpu_percent")

with c2:
    st.subheader("メモリ 使用率")
    st.line_chart(mem_series, x="time", y="mem_percent")

c3, c4 = st.columns(2)
with c3:
    st.subheader("ディスク IO (B/s)")
    st.line_chart(disk_series, x="time", y=["read_bps", "write_bps"])

with c4:
    st.subheader("ネットワーク IO (B/s)")
    st.line_chart(net_series, x="time", y=["recv_bps", "sent_bps"])

st.subheader("温度 (°C)")
st.line_chart(temp_series, x="time", y="cpu_temp_c")

st.subheader("エクスポート")
export_obj = {
    "exported_at": datetime.now().isoformat(timespec="seconds"),
    "system": sysinfo,
    "latest": cur,
    "history": hist,
}
st.download_button(
    label="スナップショットJSONをダウンロード",
    data=json.dumps(export_obj, ensure_ascii=False, indent=2),
    file_name=f"checkmechanic_snapshot_{datetime.now().strftime('%Y%m%d_%H%M%S')}.json",
    mime="application/json",
)

if opt_in:
    try:
        consent_token = get_or_create_consent_token()
        token_hash_sha256 = get_token_hash_sha256(consent_token)
        telemetry_obj = build_telemetry_payload(hist, token_hash_sha256=token_hash_sha256)

        st.markdown("送信用テレメトリ項目（プレビュー）")
        for line in telemetry_preview_lines(telemetry_obj):
            st.write(f"- {line}")
        st.json(telemetry_obj)
        st.download_button(
            label="送信用テレメトリJSONをダウンロード",
            data=json.dumps(telemetry_obj, ensure_ascii=False, indent=2),
            file_name=f"checkmechanic_telemetry_v1_{datetime.now().strftime('%Y%m%d_%H%M%S')}.json",
            mime="application/json",
        )
    except Exception as exc:
        st.error(f"送信用テレメトリJSON生成に失敗: {type(exc).__name__}: {exc}")
