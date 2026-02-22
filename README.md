# CheckMechanic

PCのベンチマーク/システム情報/温度などを分かりやすく可視化し、ユーザー同意（オプトイン）のもと
匿名化・カテゴリ化した集計データをランキング/比較指標として活用するためのプロジェクトです。

## MVP（最初にやること）
- ローカルで「見やすい可視化」が動く（PoC）
- 収集データの範囲が明確（収集しない情報も明記）
- 将来のランキング/集計に使えるスキーマがある

## PoC: Streamlit ダッシュボード
CPU/メモリ/ディスクIO/ネットワークIOを可視化します。
温度は OS により取得可否が異なり、Windows は LibreHardwareMonitor の Remote Web Server (data.json) を利用します。

### Windowsで温度を取りたい場合（LibreHardwareMonitor）
1. LibreHardwareMonitor を起動
2. Options -> Remote Web Server -> Run を ON
3. http://localhost:8085/data.json が表示できることを確認

### 起動（WSL/Linux/macOS）
```bash
cd apps/poc_streamlit
python -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
streamlit run app.py
```

### Consent / Telemetry v1 動作確認
1. `opt-in` を OFF のまま表示確認:
- 「スナップショットJSONをダウンロード」は表示される
- 「送信用テレメトリJSONをダウンロード」は表示されない
2. `opt-in` を ON にして表示確認:
- `~/.checkmechanic/consent_token` が作成される（初回のみ）
- 送信用テレメトリのプレビューと JSON ダウンロードが表示される
3. 再起動して確認:
- `~/.checkmechanic/consent_token` は再利用され、同じ `token_hash_sha256` になる
4. LHM 未起動/未接続確認:
- 画面は停止せず、理由が `warning/error` で表示される

## Windows Desktop App (WPF + SensorHelper)
`src/` 以下に .NET 8 の新規実装を追加しています。

- `src/CheckMechanic.SensorHelper`:
  - Core Temp Shared Memory を必須経路として CPU 温度を取得
  - 温度未取得時は LHM/WMI を診断情報として併記
  - `127.0.0.1:17805` で `/health`, `/v1/telemetry`, `/v1/profile`, `/v1/sensors` を提供
  - `v1/telemetry` は `cpu/memory/disk/net/gpu/battery` を返却（null許容）
  - 追加メトリクス: `cpu.clock_mhz`, `cpu.power_w`, `cpu.temp_package_c`, `cpu.temp_core_max_c`,
    `gpu.vendor`, `gpu.driver_version`, `gpu.vram_used_mb`, `gpu.vram_total_mb`, `gpu.temperature_c`, `gpu.core_clock_mhz`, `gpu.memory_clock_mhz`,
    `disk.total_gb`, `disk.free_gb`, `net.active_adapter_name`, `net.link_speed_mbps`
  - `v1/profile` 追加情報: `machine_vendor`, `machine_model`, `uptime_hours`, `gpu_driver_version`
- `src/CheckMechanic.Desktop`:
  - SensorHelper のヘルスチェックと自動起動
  - 1画面ダッシュボード（Header + KPI + Charts + System Profile + Diagnostics折りたたみ）
  - 温度・CPU・GPU・メモリ・ディスク・ネット・バッテリー・PerfScore を表示
  - チャートは温度/CPU/GPU/メモリ/ディスク/ネットのY軸付き表示
  - PerfScore はレーダー表示（CPU/Mem/Disk/Net/Thermal/GPU）＋詳細展開
  - 「終了時に Core Temp も終了」はデフォルトONでヘッダー表示、設定はローカル保存
  - Opt-in ON 時のみランキング用カテゴリJSONをローカル生成（送信は未実装）
  - 温度値が取得できない環境では「必須要件未達」を表示し、制限モードへ移行
  - Widget Mode は左下配置を既定とし、Topmost 常時表示をデフォルト化（設定でOFF可）
  - Widget のコンテキストメニューで透明度スライダー/クリック透過を切替可能（`Ctrl+Shift+W` でクリック透過トグル）
  - Core Temp 由来の既知広告ショートカット（`Goodgame*.url` など）は署名付き・直近更新ファイルのみを対象に起動時クリーンアップ

### ランキング用カテゴリ生成（ローカル）
- Opt-in が ON のときのみ `RankingProfileDto` を生成
- 生成項目はカテゴリ化済み（`os_major`, `cpu_family`, `cpu_cores`, `ram_gb_bucket`, `gpu_family`, `storage_type`, `device_class`, `temp_provider`）
- 禁止項目（hostname/username/mac/ssid/bssid/serial/uuid/smbios 等）は送信用JSONに含めない

### 対応PCの定義（temperature-required）
- `v1/telemetry` で `cpu.temp_c` が継続的に取得できること
- 取得経路は `Core Temp Shared Memory` を必須プロバイダとして使用
- `LibreHardwareMonitorLib` / `WMI` は診断用（参考値）として扱う
- `cpu.temp_c` が `null` の場合は非対応扱い（`error_code` を返却）

### 管理者権限について
- 一部環境ではセンサー取得に管理者権限が必要です
- Desktop の「管理者でSensorHelperを再起動して再試行」ボタンで昇格再試行できます
- 昇格後も `temp_c` が取得できない場合は、非対応扱いとして制限モードを維持します
- Core Temp の設定は `Options -> Settings -> Advanced -> Enable Global Shared Memory (SNMP)` を ON にしてください

### 非対応時の案内文言（統一）
- `必須要件未達: 温度取得が必要です。管理者で再試行してください。`

### 配布（S3 + CloudFront）
- Microsoft Store は使用せず、S3 + CloudFront 経由で配布します
- 手順は `docs/distribution_s3.md` を参照
- 秘密情報（AWSキー等）はリポジトリに保存しません

### Build / Run（Windows, .NET 8）
```bash
dotnet build CheckMechanic.sln
dotnet run --project src/CheckMechanic.SensorHelper/CheckMechanic.SensorHelper.csproj
dotnet run --project src/CheckMechanic.Desktop/CheckMechanic.Desktop.csproj
```

### Installer build（Zip + Setup.exe, WiX Burn）
- 詳細は `docs/INSTALLER.md` を参照
- 生成物: `dist/CheckMechanic-Setup.zip`（中に `Setup.exe`）

```powershell
pwsh ./scripts/build_installer_zip.ps1 -Configuration Release -Version 1.0.0 -PublishSingleFile $true
```

- 既定では Core Temp の自動導入は行わない（手動インストール必須）
- Setup は Core Temp の導入有無のみ検出し、未導入なら公式URL案内して終了
- 自動導入を行わない理由: 追加広告ショートカット（例: `Goodgame Empire.url`）の回避
- 念のため Desktop 起動時に既知パターンの不要 `.url` を後始末（誤削除防止のため署名+更新時刻で判定）
- Core Temp の再配布条件はライセンス要確認（既定は同梱しない）

### 更新チェック（Desktop）
- Desktop 起動時に `latest.json` を非同期取得し、新版があればヘッダ右上に `UPDATE` バッジを表示
- `latest.json` は固定URL（`MainWindow.xaml.cs` の `UpdateManifestUrl` 定数）から取得
- 想定スキーマ:
```json
{
  "version": "1.0.2",
  "download_url": "https://.../checkmechanic/1.0.2/Setup.exe",
  "release_notes_url": "https://.../releases/1.0.2",
  "sha256": "hex..."
}
```
- バッジクリックで Setup.exe を `%LocalAppData%\\CheckMechanic\\updates\\<version>\\Setup.exe` に保存し、SHA256検証後に Burn を `/passive /norestart /log` で起動

#### 配布時の `latest.json` 生成例
1. `scripts/build_installer_zip.ps1` で `dist/installer/Setup.exe` を生成
2. `scripts/new_update_manifest.ps1` で `sha256` 付き `latest.json` を生成
3. `Setup.exe` と `latest.json` を公開URLへ配置

```powershell
pwsh ./scripts/build_installer_zip.ps1 -Configuration Release -Version 1.0.2 -PublishSingleFile $true
pwsh ./scripts/new_update_manifest.ps1 `
  -Version 1.0.2 `
  -DownloadUrl "https://downloads.example.com/checkmechanic/1.0.2/Setup.exe" `
  -ReleaseNotesUrl "https://downloads.example.com/checkmechanic/1.0.2/notes.html"
```

#### WSLから一括リリース（build + manifest + upload + invalidation）
```bash
./scripts/release.sh \
  --version 1.0.2 \
  --bucket checkmechanic-release-bucket \
  --distribution-id E1JOK9M9AG4WIM \
  --domain checkmechanic.apiron.jp
```

### API quick check
```powershell
irm http://127.0.0.1:17805/health
irm http://127.0.0.1:17805/v1/telemetry
irm http://127.0.0.1:17805/v1/profile
irm http://127.0.0.1:17805/v1/sensors
```

### テスト
```bash
dotnet test tests/CheckMechanic.Shared.Tests/CheckMechanic.Shared.Tests.csproj
```
