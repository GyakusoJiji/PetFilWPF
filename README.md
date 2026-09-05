# PetFil — PET ボトル・フィラメント製造機コントローラ

PET ボトルからフィラメントを作る押出・巻き取り機を、PC から操作するためのホストアプリケーションです。
[Printrun / Pronterface](https://github.com/kliment/Printrun) のフォークで、シリアル通信の中核（`printcore`）と温度グラフのウィジェットをそのまま流用し、
3D プリンタ向けの機能（スライサ連携・3D ビューア・プレータ・SD 印刷など）を取り除いています。

![PetFil](assets_raw/icons/pronterface.png)

## 機械の前提

| 項目 | 前提 |
|---|---|
| ファームウェア | Marlin 互換（`M104` / `M105` / `M155` / `M211` / `G91` / `G1` / `M410`） |
| ヒーター | 1 系統（ノズルのみ）。ベッドヒーターは使いません |
| 駆動軸 | **X 軸を巻き取りモーターとして使用**。E 軸は使いません |

## できること

- **接続** — シリアルポートの自動検出、ボーレート選択。`host:port` 形式を入力すると TCP 接続もできます
- **ノズル温度** — 任意の目標温度を設定 / OFF、現在温度のゲージ表示
- **温度履歴** — 直近 2 分間を 1 秒刻みで波形表示（縦線 1 本 = 20 秒）
- **巻き取り** — X 軸を指定した速度（mm/min）で連続駆動。数値入力とスライダーのどちらでも設定でき、運転中でも速度変更可、積算送り量を表示
- **G-code コンソール** — 任意のコマンド送信（↑↓ で履歴）と受信ログ
- **非常停止** — 巻き取りを止め、キューに残った移動を破棄して全ヒーターを OFF（右クリックで `M112` halt）

## 巻き取りの仕組み

Marlin には「一定速度で回し続ける」コマンドがないため、相対移動（`G91` + `G1 X…`）の短い区間を 1 秒周期で投入し続けます。
1 区間の長さは 1 周期分より 20% 長く取り、モーションが途切れないようにしています。
送りすぎを防ぐため、`printcore` の優先キューに 3 件以上溜まっている間は投入を止めます（キュー深度による自己ペーシング）。
`M211 S0` でソフトウェアエンドストップを無効にし、毎区間 `G92 X0` で座標をリセットするため、長時間運転しても座標が発散しません。

## インストールと実行

Python 3.8〜3.13 に対応（wxPython の対応状況に依存）。

### Windows

```cmd
> git clone <このリポジトリ>
> cd PetBottleFirament
> release_windows.bat
```

手動で環境を作る場合:

```cmd
> py -3.11 -m venv v3
> v3\Scripts\activate
> pip install --upgrade pip setuptools wheel
> pip install cython -r requirements.txt
> python setup.py build_ext --inplace
> python petfil.py
```

`setup.py build_ext` には MSVC の C++ ビルドツールと Windows SDK が必要です。
未インストールでも動作しますが、G-code パーサが純 Python 実装にフォールバックし、やや遅くなります。

### Linux / macOS

```shell
$ python3 -m venv venv && . ./venv/bin/activate
(venv) $ pip install --upgrade pip setuptools
(venv) $ pip install cython -r requirements.txt
(venv) $ python setup.py build_ext --inplace
(venv) $ python petfil.py
```

### 起動オプション

```
petfil [OPTIONS]

  -h, --help          このヘルプを表示して終了
  -V, --version       バージョンを表示して終了
  -v, --verbose       ログを詳細にする
  -p, --port=PORT     シリアルポート、または host:port を初期選択
  -a, --autoconnect   起動時に自動接続する
```

設定（ポート・ボーレート・目標温度・巻き取り速度・ウィンドウサイズ）は
`platformdirs` のユーザー設定ディレクトリ配下 `PetFil/config.json` に保存されます。

## 実機なしで試す

`tools/fake_marlin.py` が Marlin の応答を模した TCP サーバとして動きます。

```shell
$ python tools/fake_marlin.py --port 8080
$ python petfil.py --port 127.0.0.1:8080 --autoconnect
```

ヒーターは目標温度に向かって毎秒 8 °C ずつ上昇し、`M155` の自動レポートにも応答します。

## テスト

```shell
$ python -m unittest discover tests
```

`printrun/petfil/controller.py` は GUI ツールキットを import しないため、
wxPython なしの環境でもロジックのテストが実行できます。

## 構成

```
petfil.py                     ランチャ
printrun/petfil/
    controller.py             機械ロジック（wx 非依存）— 接続・温度・巻き取り・非常停止
    gui.py                    wxPython の画面
    config.py                 JSON 設定
printrun/printcore.py         シリアル/TCP 通信エンジン（upstream Printrun 由来）
printrun/gui/graph.py         温度グラフ（upstream Printrun 由来）
tools/fake_marlin.py          実機なし検証用の擬似ファームウェア
tests/                        unittest
```

# LICENSE

```
Copyright (C) 2011-2024 Kliment Yanev, Guillaume Seguin, and the other contributors listed in CONTRIBUTORS.md

Printrun is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

Printrun is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with Printrun.  If not, see <http://www.gnu.org/licenses/>.
```

PetFil is a fork of Printrun and is distributed under the same terms (GPLv3).
All scripts should contain this license note; files where it is difficult to state it
(such as images) are distributed under the same terms.
