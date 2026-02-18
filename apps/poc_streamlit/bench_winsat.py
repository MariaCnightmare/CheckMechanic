from __future__ import annotations

import argparse
import os
import subprocess
import sys
import xml.etree.ElementTree as ET


def run_winsat(xml_path: str) -> int:
    cmd = ["winsat", "formal", "-xml", xml_path]
    print("Running:", " ".join(cmd))
    p = subprocess.run(cmd, capture_output=True, text=True)
    print(p.stdout)
    if p.returncode != 0:
        print(p.stderr, file=sys.stderr)
    return p.returncode


def extract_scores(xml_path: str) -> dict:
    tree = ET.parse(xml_path)
    root = tree.getroot()

    out = {}
    for elem in root.iter():
        tag = elem.tag.lower()
        text = (elem.text or "").strip()
        if not text:
            continue

        if any(k in tag for k in ["winspr", "systemscore", "cpu", "memory", "disk", "graphics", "d3d"]):
            try:
                val = float(text)
                out[elem.tag] = val
            except Exception:
                pass

    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--xml", default=os.path.abspath("winsat_result.xml"))
    ap.add_argument("--run", action="store_true", help="WinSATを実行してXMLを生成する")
    args = ap.parse_args()

    if args.run:
        rc = run_winsat(args.xml)
        if rc != 0:
            return rc

    if not os.path.exists(args.xml):
        print("XML not found:", args.xml, file=sys.stderr)
        return 2

    scores = extract_scores(args.xml)
    print("XML:", args.xml)
    print("Extracted keys:", len(scores))
    for k, v in sorted(scores.items()):
        print(f"{k}: {v}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

