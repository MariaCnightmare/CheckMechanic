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
  - LibreHardwareMonitorLib を使って CPU 温度を取得
  - 温度未取得時は WMI (ACPI Thermal Zone) へフォールバック
  - `127.0.0.1:17805` で `/health`, `/v1/telemetry`, `/v1/sensors` を提供
- `src/CheckMechanic.Desktop`:
  - SensorHelper のヘルスチェックと自動起動
  - 温度表示・CPU使用率表示・ステータス表示・再接続/再起動ボタン・簡易ログ
  - 温度値が取得できない環境では「必須要件未達」を表示し、制限モードへ移行

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
