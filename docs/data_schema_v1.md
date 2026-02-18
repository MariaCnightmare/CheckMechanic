# Data Schema v1（案）

## 方針
- ランキング/比較に必要な「最小限」のみ送る
- 個体識別につながりやすい値（ホスト名/MAC/UUID/シリアル等）は **送信禁止**
- 公開は集計中心 + kしきい値（人数不足カテゴリは非表示）

## 送信ペイロード（JSON例）

```json
{
  "schema_version": "1.0",
  "submitted_at": "2026-02-18T12:34:56+09:00",
  "consent": {
    "opt_in": true,
    "consent_version": "2026-02-18",
    "data_level": "basic",
    "token_hash_sha256": "HEX_STRING"
  },
  "system": {
    "os": { "family": "Windows", "major": 11 },
    "cpu": {
      "vendor": "Intel",
      "family": "Core i7",
      "generation_bucket": "12-14",
      "core_count_bucket": "8-16"
    },
    "gpu": {
      "vendor": "NVIDIA",
      "tier_bucket": "mid",
      "vram_gb_bucket": "8-12"
    },
    "memory": {
      "total_gb_bucket": "32",
      "speed_mhz_bucket": "5600-6400"
    },
    "motherboard": {
      "chipset": "Z790",
      "form_factor": "ATX"
    },
    "storage": {
      "system_drive_type": "NVMe",
      "system_drive_size_gb_bucket": "512-1024"
    }
  },
  "bench": {
    "suite": "winsat",
    "runs": 3,
    "score": {
      "cpu": 12345.6,
      "memory": 23456.7,
      "disk": 34567.8,
      "graphics": 4567.8
    },
    "score_aggregate": {
      "overall": 15234.5,
      "method": "median"
    }
  },
  "telemetry_summary": {
    "load_test": {
      "duration_sec": 180,
      "cpu_util_avg": 92.1,
      "cpu_util_p95": 99.2,
      "cpu_temp_c_max": 87.0,
      "cpu_temp_c_p95": 83.0,
      "throttle_suspected": true
    }
  }
}

