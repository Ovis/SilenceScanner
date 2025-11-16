# SilenceScanner

## 概要

SilenceScannerは、FLACファイル内の無音区間を自動検出するコマンドラインツールです。  
CDから取り込んだFLACファイルが正常に取り込まれているかどうかを簡易的にチェックすることが可能です。  

## 主な機能

- **無音区間の検出**: ffmpegの`silencedetect`フィルタを使用した高精度な無音検出
- **ハイパスフィルタ**: 低周波ノイズ（デフォルト70Hz以下）を除去して誤検出を防止
- **エッジ除外**: 曲の先頭・末尾の無音は自動的に除外（中間の無音のみを検出）
- **ジャンルフィルタ**: 特定ジャンルのファイルのみを処理対象にする機能
- **リアルタイム進捗表示**: 処理状況と検出結果をリアルタイムで表示
- **TSV出力**: 検出結果をタブ区切り形式で逐次出力
- **再帰的スキャン**: 指定フォルダ内のすべてのFLACファイルを自動検索

## 必要要件
- ffmpeg と ffprobe（システムにインストール済みでPATHが通っていること）

## インストール

Release からダウンロードしたZIPファイルを展開

## 使用方法

### 基本的な実行

```
./SilenceScanner <folder>
```

### 実行例

```
# カレントディレクトリのFLACファイルをスキャン
./SilenceScanner -- .

# 特定フォルダをスキャン
./SilenceScanner -- "D:\Music\Albums"

# オプションを指定してスキャン
./SilenceScanner -- "D:\Music" --silence 3.0 --thresh -50 --out results.tsv

# 特定ジャンルのみをスキャン
./SilenceScanner -- "D:\Music" --genre "Soundtrack"
```

## コマンドオプション

| オプション | デフォルト値 | 説明 |
|-----------|-------------|------|
| `<folder>` | (必須) | スキャン対象のフォルダパス |
| `--silence` | `2.0` | 検出する最小無音時間（秒） |
| `--thresh` | `-60.0` | 無音と判定する音量閾値（dB） |
| `--hpf` | `70.0` | ハイパスフィルタのカットオフ周波数（Hz） |
| `--edgeeps` | `0.02` | 先頭・末尾として除外する範囲（秒） |
| `--genre` | なし | フィルタするジャンル名（部分一致） |
| `--out` | `silence_candidates.tsv` | 出力ファイルパス |
| `--showmax` | `10` | 画面に表示する最大検出件数 |

### オプション詳細

#### `--silence <秒数>`
最小無音時間を指定します。この秒数以上続く無音区間のみを検出します。

```bash
# 3秒以上の無音のみを検出
./SilenceScanner -- "D:\Music" --silence 3.0
```

#### `--thresh <dB値>`
無音と判定する音量の閾値を指定します。この値以下の音量を無音とみなします。

```bash
# -50dB以下を無音とする（より厳しい基準）
./SilenceScanner -- "D:\Music" --thresh -50
```

#### `--hpf <Hz>`
ハイパスフィルタのカットオフ周波数を指定します。この周波数以下の低周波ノイズを除去します。

```bash
# 100Hz以下をカット
./SilenceScanner -- "D:\Music" --hpf 100
```

#### `--edgeeps <秒数>`
曲の先頭・末尾から何秒の範囲を「エッジ」として除外するかを指定します。この範囲内の無音は検出結果から除外されます。

```bash
# 先頭・末尾0.05秒の無音を除外
./SilenceScanner -- "D:\Music" --edgeeps 0.05
```

#### `--genre <ジャンル名>`
メタデータのGenreタグが指定した文字列を含むファイルのみを処理します。

```bash
# "Soundtrack"を含むファイルのみをスキャン
./SilenceScanner -- "D:\Music" --genre "Soundtrack"

# "Jazz"を含むファイルのみをスキャン
./SilenceScanner -- "D:\Music" --genre "Jazz"
```

#### `--out <ファイルパス>`
検出結果を出力するTSVファイルのパスを指定します。

```bash
./SilenceScanner -- "D:\Music" --out "my_results.tsv"
```

#### `--showmax <件数>`
画面に表示する検出ファイルの最大件数を指定します。ウィンドウサイズを超える場合は自動的に調整されます。

```bash
# 最新20件まで表示
./SilenceScanner -- "D:\Music" --showmax 20
```

## 出力形式

検出結果はTSVファイル（タブ区切り）で出力されます。

### TSVファイルの構造

```
FilePath	StartSec	EndSec	DurationSec
D:\Music\album\track01.flac	45.123	47.456	2.333
D:\Music\album\track02.flac	120.789	123.012	2.223
```

| カラム | 説明 |
|--------|------|
| `FilePath` | 無音が検出されたファイルのフルパス |
| `StartSec` | 無音開始位置（秒） |
| `EndSec` | 無音終了位置（秒） |
| `DurationSec` | 無音の長さ（秒） |

### 画面表示

処理中は以下の情報がリアルタイムで表示されます：

```
Progress 50/100  50.0%  [####################--------------------]
Current: D:\Music\album\track01.flac
Found: 3 (showing last 3)
[FLAG] D:\Music\album\track01.flac (1 segment)
[FLAG] D:\Music\album\track05.flac (2 segment)
[FLAG] D:\Music\album\track08.flac (1 segment)
```

- **Progress**: 全体の進捗状況
- **Current**: 現在処理中のファイル
- **Found**: 無音が検出されたファイルの総数と表示件数

## エラーハンドリング

プログラムは以下のエラーを適切に処理します：

- **ffprobe/ffmpegが見つからない**: 起動時にチェックし、明確なエラーメッセージを表示
- **ファイルアクセスエラー**: 読み取り不可のファイルはスキップして処理を続行
- **出力ファイルの書き込みエラー**: エラーメッセージを表示して処理を中断
- **破損したFLACファイル**: エラーログに記録して次のファイルへ進む

## ライセンス

MIT License


## トラブルシューティング

### ffmpeg/ffprobeが見つからない

```
ERROR: ffprobe not found. Please install ffmpeg toolkit.
```

→ ffmpegをインストールし、PATHに追加してください。

**Windows**:
- [ffmpeg公式サイト](https://ffmpeg.org/download.html)からダウンロード
- 環境変数PATHにffmpeg/binフォルダを追加
