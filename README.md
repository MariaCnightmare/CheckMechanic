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

