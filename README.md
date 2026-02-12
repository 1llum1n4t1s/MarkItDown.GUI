# MarkItDown.GUI

シンプルなドラッグ&ドロップ操作で、様々なファイル形式をMarkdown形式に変換するWindowsアプリケーションです。WebページのスクレイピングによるJSON出力にも対応しています。

<img width="689" height="539" alt="image" src="https://github.com/user-attachments/assets/65efa452-559f-44c3-ba6f-3d62255d6be2" />


## 特徴

- **簡単操作**: ファイルやフォルダをウィンドウにドロップするだけで自動変換
- **多様なファイル形式に対応**: Office文書、PDF、画像、音声、テキスト、アーカイブなど幅広く対応
- **Webスクレイピング**: URLを入力するだけでWebページの内容をJSON形式で抽出・保存
  - Reddit: JSON API による高速取得
  - X/Twitter: ユーザーのタイムライン全件取得＋画像オリジナル画質ダウンロード（セッション永続化対応）
  - その他のサイト: HTTP + Claudeガイド型でブラウザを起動せずに高速取得（ページネーション対応）
- **自動環境構築**: Python環境、FFmpegを初回起動時に自動でダウンロード・セットアップ
- **OSの環境を汚さないポータブル設計**: Python・FFmpeg・Node.js・Claude Code CLIなど全ての依存コンポーネントをアプリ内の `lib/` フォルダに格納。システムのPATH・レジストリ・既存のPython環境には一切影響しません。アンインストール時もフォルダ削除だけで完全にクリーンアップできます
- **Claude AI連携（オプション）**: Claude Code CLIを使用したMarkdown整形・まとめ、スクレイピング戦略分析を統合。メイン画面のラジオボタンでON/OFF切替可能（デフォルトOFF）
- **3種類のファイル出力**: 元データ・整形済・まとめ済の3ファイルを自動生成（Claude利用時。元データとの比較で整形による情報損失をチェック可能）
- **並列処理**: 最大3ファイルの同時変換で高速処理
- **重複処理の回避**: 同じファイルの再処理をキャッシュでスキップ
- **パッケージ自動更新**: 起動時にPythonパッケージ（markitdown、playwright、httpx）の最新バージョンを自動確認・更新
- **自動アップデート**: アプリ起動時に最新版を自動チェック・適用
- **モダンなUI**: Avalonia UIを使用した軽量で使いやすいインターフェース（処理中オーバーレイによる進捗表示付き）

## ダウンロード

最新版は以下のGitHub Releasesページからダウンロードできます。

**[📥 ダウンロードページ（GitHub Releases）](https://github.com/1llum1n4t1s/MarkItDown.GUI/releases)**

`Setup.exe` をダウンロードして実行してください。

## サポートしているファイル形式

### テキスト・文書ファイル
- `.txt`, `.html`, `.htm`, `.csv`, `.xml`

### Office文書
- `.docx`, `.doc`, `.xlsx`, `.xls`, `.pptx`, `.ppt`

### PDFファイル
- `.pdf`

### 画像ファイル
- `.jpg`, `.jpeg`, `.png`, `.gif`, `.bmp`, `.tiff`, `.tif`, `.webp`

### 音声ファイル
- `.mp3`, `.wav`, `.flac`, `.aac`, `.ogg`

### アーカイブファイル
- `.zip`, `.rar`, `.7z`, `.tar`, `.gz`

> **注意**: `.md` と `.json` はアプリの出力形式と同一のため、フォルダドロップ時の変換対象から自動的に除外されます。

## 使い方

### ファイル変換

1. アプリケーションを起動します（初回はPython環境・FFmpegの自動セットアップが行われます）
2. Claude AIを使用する場合は、メイン画面の「Claude AI」ラジオボタンを「使用する」に切り替えます（Node.js・Claude Code CLIの自動セットアップと認証が実行されます）
3. 変換したいファイルまたはフォルダをウィンドウにドラッグ&ドロップします
4. 自動的に変換が開始され、元のファイルと同じ場所にMarkdownファイル（`.md`）が生成されます

#### 出力ファイル

Claude利用可能時は以下の3ファイルが出力されます（Claude未使用時は元データのみ）：

| 種別 | ファイル名 | 内容 |
|---|---|---|
| 元データ | `ファイル名_元データ_YYYYMMDDHHmmss.md` | MarkItDown変換後の生データ |
| 整形済 | `ファイル名_整形済_YYYYMMDDHHmmss.md` | Claudeで書式を整形したデータ |
| まとめ済 | `ファイル名_まとめ済_YYYYMMDDHHmmss.md` | Claudeでまとめたデータ |

フォルダをドロップした場合は、フォルダ内のサポート対象ファイルを再帰的に検索し、最大3ファイルずつ並列で変換します。

### Webスクレイピング

1. ウィンドウ上部のURL入力欄にWebページのURLを入力します
2. 「🌐 抽出」ボタンをクリックします
3. 出力先フォルダを選択すると、スクレイピングが実行されます

#### 出力ファイル

Claude利用可能時は以下の3ファイルが出力されます（Claude未使用時は元データのみ）：

| 種別 | ファイル名 | 内容 |
|---|---|---|
| 元データ | `ファイル名_元データ_YYYYMMDDHHmmss.json` | スクレイピング結果の生JSON |
| 整形済 | `ファイル名_整形済_YYYYMMDDHHmmss.json` | Claudeで構造化・整形したJSON |
| まとめ済 | `ファイル名_まとめ済_YYYYMMDDHHmmss.md` | ClaudeでまとめたMarkdown |

処理中はオーバーレイに現在の進行状況（依存パッケージ確認 → スクレイピング → Claude JSON整形 → Claudeまとめ）がリアルタイムで表示されます。

#### 対応サイト

| サイト種別 | 取得方法 | 特徴 |
|---|---|---|
| Reddit | JSON API | 投稿・コメントを高速取得 |
| X/Twitter | Playwright（専用スクリプト） | ユーザータイムライン全件取得、画像オリジナル画質DL、セッション永続化 |
| その他 | HTTP + Claude | ブラウザ不使用、HTTPで高速取得（ページネーション対応） |

#### X/Twitter スクレイピング

X/Twitter のユーザーページURL（例: `x.com/username`）を入力すると、専用スクレイパーが起動します。

**主な機能：**
- ユーザーの全オリジナルツイートを取得（リツイートは除外）
- 添付画像をオリジナル画質（`name=orig`）で自動ダウンロード
- セッション永続化により2回目以降はログイン不要
- BOT検出回避のためのランダムスクロール・迂回ナビゲーション

**初回利用時：**
1. ブラウザウィンドウが自動で開きます（ヘッドレスではなく表示モード）
2. X/Twitterにログインしてください（最大10分間待機）
3. ログインが検出されると自動的にスクレイピングが開始されます
4. セッション情報は `lib/playwright/x_profile/` に保存され、次回以降は自動ログインされます

> **注意**: X/Twitterのデータは大量になるため、Claude整形・まとめ処理はスキップされます。元データ（JSON）のみ出力されます。

**出力ファイル：**

| ファイル | 内容 |
|---|---|
| `{username}_元データ_YYYYMMDDHHmmss.json` | 全ツイートのJSON（テキスト・メトリクス・画像情報） |
| `{username}/` フォルダ | ダウンロードされた画像ファイル群 |

#### Claudeガイド型スクレイピング（HTTPベース）

HTTP（requests + BeautifulSoup）とClaude（Claude Code CLI）を組み合わせて、ブラウザを起動せずにWebページを取得・解析します：

- ページ構造のDOM統計分析（タグ出現数、class名頻度、ID一覧）
- ClaudeによるCSSセレクタと抽出戦略の動的決定
- 戦略ベースのコンテンツ抽出（リスト・記事・汎用ページに対応）
- ページネーション（「次へ」リンクの自動検出・追跡）
- メタデータ・JSON-LD構造化データの抽出

Claude Code CLI（`node cli.js -p "<prompt>"`）を使用して、HTMLの構造を分析し最適なスクレイピング戦略を決定します。ブラウザを使わないため起動が高速で、リソース消費も軽量です。

> **注意**: JavaScriptで動的にレンダリングされるコンテンツ（SPAサイト等）は取得できない場合があります。X/Twitterのスクレイピングでは従来通りPlaywright（ブラウザ）が使用されます。

## Claude AI連携機能（オプション）

Claude Code CLI を使用して、以下のAI連携機能を提供します。**デフォルトはOFFで、メイン画面のラジオボタンで有効化できます。**

- **Markdown整形**: 全ファイル形式の変換結果を、Claudeで自動的にきれいなMarkdown形式に整形します（元データは別ファイルで保持）
- **自動まとめ**: 変換結果・スクレイピング結果をClaudeで自動分析・まとめし、Markdownファイルとして出力します
- **スクレイピング戦略分析**: Webスクレイピング時にページ構造（DOM統計、HTMLサンプル）を分析し、最適なCSSセレクタと抽出戦略を動的に決定します
- **スクレイピングJSON整形**: Webスクレイピング結果のJSONを構造化・整形します（大きなJSONはチャンク分割で処理）

### 有効化方法

1. メイン画面の「Claude AI」ラジオボタンを「使用する」に切り替えます
2. Node.js（v20.18.1）が自動的にダウンロード・展開されます
3. Claude Code CLI（`@anthropic-ai/claude-code`）がnpmで自動インストールされます
4. ブラウザベースのOAuth認証画面が開きます（初回のみ）
5. 認証完了後、接続検証が行われ、AI機能が利用可能になります

> **設定の保存**: Claude AI の使用設定は `appsettings.xml` に保存され、次回起動時に復元されます。前回ONで終了した場合は、起動時に自動的にセットアップが実行されます。

### 設定のカスタマイズ

設定ファイル `appsettings.xml` で以下をカスタマイズできます：

```xml
<AppSettings>
  <UseClaudeAI>false</UseClaudeAI>
</AppSettings>
```

| 設定項目 | 説明 | デフォルト値 |
|---|---|---|
| `UseClaudeAI` | Claude AIを使用するかどうか | `false` |

### 注意事項

- **認証**: Claude Code CLIのOAuth認証にはAnthropicアカウントが必要です
- **ネットワーク**: Claude AIはクラウドベースのため、インターネット接続が必要です
- **初回セットアップ**: Node.jsとClaude Code CLIのダウンロードに数分かかる場合があります
- Claude AIが無効でも、ファイル変換やWebスクレイピングの基本機能は正常に動作します

## 自動環境構築

初回起動時に以下のコンポーネントが自動的にダウンロード・セットアップされます。

| コンポーネント | 用途 | 配置先 |
|---|---|---|
| 埋め込みPython 3.10+ | MarkItDownライブラリの実行環境 | `lib/python/python-embed/` |
| FFmpeg | 音声ファイルの処理 | `lib/ffmpeg/` |
| Node.js v20.18.1 | Claude Code CLIの実行環境（Claude有効時） | `lib/nodejs/` |
| Claude Code CLI | AI連携機能（Claude有効時） | `lib/npm/node_modules/` |
| markitdown（最新版） | ファイル変換ライブラリ（pip自動インストール・更新） | Python site-packages |
| playwright | Webスクレイピング用ブラウザ自動化（pip自動インストール・更新） | Python site-packages |
| httpx | X/Twitter画像ダウンロード用HTTPクライアント（pip自動インストール・更新） | Python site-packages |

### OSの環境を汚さないポータブル設計

全ての依存コンポーネントはアプリケーションフォルダ内の `lib/` 配下に自己完結して格納されます。

- **Python**: 公式の埋め込み版（Windows Embeddable Package）を使用。システムにインストール済みのPython環境には一切干渉しません
- **Pythonパッケージ**: markitdown、playwright、httpx等は埋め込みPython専用の site-packages にインストールされます
- **FFmpeg**: `lib/ffmpeg/` に格納。システムのPATHやレジストリは変更しません
- **Node.js & Claude Code CLI**: `lib/nodejs/` および `lib/npm/` に格納。システムにインストールされたNode.jsとは独立して動作します

アンインストール時はアプリケーションフォルダを削除するだけで、OS上に痕跡を残しません。

起動時にこれらのPythonパッケージの最新バージョンが自動的に確認・更新されます。

> **技術的補足**: markitdown 0.1.x は `onnxruntime<=1.20.1` を要求しますが、Python 3.14 では onnxruntime 1.24.1+ しか利用できないため、依存パッケージを先にインストールした後、markitdown本体を `--no-deps` オプションでインストールしています。実際にはonnxruntime 1.24.1でも正常に動作します。

## 動作環境

- Windows 11 (ビルド 26200以降)
- .NET 10.0 Runtime（インストーラーに含まれています）

## 技術仕様

- **フレームワーク**: Avalonia UI 11.3 / .NET 10.0
- **言語**: C# 14 / Python 3.10+
- **アーキテクチャ**: MVVM (Model-View-ViewModel)
- **ファイル変換**: Microsoft MarkItDown（Python）
- **AI連携**: Claude Code CLI（`@anthropic-ai/claude-code`） via Node.js — オプション
- **Webスクレイピング**: HTTP + BeautifulSoup + Claude（構造分析）による汎用スクレイピング、X/Twitter専用Playwrightスクレイパー
- **自動更新**: Velopack
- **ログ**: ZLogger（ローリングファイル出力）

## ライセンス

このプロジェクトはMITライセンスの下で公開されています。詳細は [LICENSE](LICENSE) ファイルをご覧ください。
