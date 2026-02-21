# Consent / Opt-in / Delete

## 同意の原則
- 初期状態は OFF（未同意）
- `apps/poc_streamlit/app.py` では opt-in が ON のときだけ「送信用テレメトリJSON」を生成/表示/ダウンロードする
- opt-in が OFF のときは既存の「スナップショットJSON」ダウンロードのみ有効

## 削除トークン
- 初回のみ 32 bytes のランダムトークンを生成してローカル保存する
- 保存先: `~/.checkmechanic/consent_token`
- 保存形式: 64 桁の hex 文字列
- 送信用 JSON にはトークン本体を含めず `token_hash_sha256` のみを含める

## 送信データの最小化
- 送信データは `docs/data_schema_v1.md` のカテゴリ化スキーマに合わせる
- 個体識別につながる項目（ホスト名、ユーザー名、IP、MAC、SSID/BSSID、UUID、シリアル等）は送信 JSON に含めない
- 温度などの時系列データは生ログを送らず、avg/p95/max などの要約のみ送る

## 例外表示
- 温度取得（`psutil.sensors_temperatures`）失敗は `st.warning` で表示
- LibreHardwareMonitor (`/data.json`) 取得失敗は `st.error` で表示
- 項目が取れない場合は `None` のまま画面表示/JSON生成を継続する
