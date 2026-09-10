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

- **接続** — シリアルポートの自動検出、ボーレート選択
- **ノズル温度** — 任意の目標温度を設定 / OFF、現在温度のゲージ表示
- **温度履歴** — 直近 2 分間を 1 秒刻みで波形表示（縦線 1 本 = 20 秒）
- **巻き取り** — X 軸を指定した速度（mm/min）で連続駆動。数値入力とスライダーのどちらでも設定でき、運転中でも速度変更可、積算送り量を表示
- **G-code コンソール** — 任意のコマンド送信と受信ログ
- **運転条件の保存** — 目標温度と巻き取り速度を保存・呼び出し。次回起動時は前回の最終設定から始まります
- **非常停止** — 巻き取りを止め、送信待ちの移動を破棄し、`M410` で先読み済みの移動も打ち切ってヒーターを OFF（右クリックで `M112` halt）

## 巻き取りの仕組み

Marlin には「一定速度で回し続ける」コマンドがないため、相対移動（`G91` + `G1 X…`）の短い区間を 1 秒周期で投入し続けます。
1 区間の長さは 1 周期分より 20% 長く取り、モーションが途切れないようにしています。
送りすぎを防ぐため、`printcore` の優先キューに 3 件以上溜まっている間は投入を止めます（キュー深度による自己ペーシング）。
`M211 S0` と `M121` でソフト/ハード両方のエンドストップ判定を止め、毎区間 `G92` で座標をリセットするため、長時間運転しても座標が発散しません。
巻き取り方向は X の負側なので、区間ごとに移動量ぶんずらした位置を原点にして、移動先がちょうど 0 になるようにしています（負の座標はファームウェアに切り捨てられるため）。

## ビルドと実行

.NET 8 SDK が必要です（実行だけなら .NET 8 デスクトップランタイム）。

```cmd
> git clone <このリポジトリ>
> cd "Pet Bottle Recycler WPF"
> dotnet build PetFil.sln -c Release
> PetFil.Wpfin\Release
et8.0-windows\PetFil.Wpf.exe
```

開発中は `dotnet run --project PetFil.Wpf` でそのまま起動できます。
ランタイムを同梱した単体の exe が要る場合:

```cmd
> dotnet publish PetFil.Wpf/PetFil.Wpf.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

### 設定の保存

目標温度と巻き取り速度は `%APPDATA%\PetFil\settings.json` に保存されます。

- `Last` — 終了時に自動で記録され、次回起動時の初期値になります
- `Saved` — 「保存」ボタンを押したときだけ更新されます。「呼び出し」で入力欄に戻せます

「呼び出し」は入力欄に値を入れるだけで、プリンタには送りません。温度は「設定」ボタンで反映してください。

## テスト

```cmd
> dotnet test PetFil.sln
```

`PetFilController` と `AppSettings` は WPF に依存しないため、シリアルポートや画面なしでロジックを検証できます。

## 構成

```
PetFil.sln
PetFil.Wpf/
    PetFilController.cs       機械ロジック — 接続・温度・巻き取り（UI 非依存）
    AppSettings.cs            settings.json の読み書き
    MainWindow.xaml(.cs)      画面
PetFil.Wpf.Tests/             xUnit のテスト
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
