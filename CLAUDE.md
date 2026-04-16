# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# ビルド（ソリューション全体）
dotnet build MarkItDown.GUI.slnx --configuration Release

# デバッグビルド
dotnet build MarkItDown.GUI.slnx

# 実行（デバッグ）
dotnet run --project MarkItDown.GUI/MarkItDown.GUI.csproj

# 発行（win-x64 AOT）
dotnet publish MarkItDown.GUI/MarkItDown.GUI.csproj -c Release -o artifacts/publish -r win-x64
```

テストプロジェクトは存在しない。単一プロジェクト構成（`MarkItDown.GUI.slnx` → `MarkItDown.GUI.csproj`）。

## Architecture

**Avalonia UI 11.3 / .NET 10.0 / C# 14** のデスクトップアプリ。MVVMパターン。DIコンテナは使わず手動注入。

### エントリポイントの流れ（Program.cs）

1. `VelopackApp.Build().Run()` — Velopackフック処理（更新適用後の再起動、旧スタートアップ登録の掃除）
2. Mutex で多重起動防止
3. Logger/AppSettings 初期化 → Avalonia起動
4. `App.OnFrameworkInitializationCompleted()` でバックグラウンド更新チェック（`CheckForUpdateInBackground()`）

### ディレクトリ構成（重要なもの）

```
MarkItDown.GUI/
├── Program.cs              # エントリポイント、Velopack統合
├── App.axaml.cs            # ライフサイクル管理、リソースクリーンアップ、起動時更新チェック
├── MainWindow.axaml(.cs)   # UI定義、ドラッグ&ドロップ
├── ViewModels/
│   ├── ViewModelBase.cs    # INotifyPropertyChanged基底クラス
│   └── MainWindowViewModel.cs  # メインロジック（ファイル処理、スクレイピング、Claude連携）
├── Services/
│   ├── AppSettings.cs          # XML設定（%LOCALAPPDATA%/MarkItDown.GUI/appsettings.xml）
│   ├── AppPathHelper.cs        # パス解決（Velopack環境 vs 開発環境の自動判定）
│   ├── Logger.cs               # SuperLightLoggerラッパー（Release時はWarning以上のみ出力）
│   ├── PythonEnvironmentManager.cs  # 埋め込みPythonのDL/セットアップ
│   ├── PythonPackageManager.cs      # pipパッケージ管理
│   ├── FfmpegManager.cs            # FFmpegのDL/セットアップ
│   ├── FileProcessor.cs            # ファイル→Markdown変換オーケストレーション
│   ├── MarkItDownProcessor.cs      # Python markitdownラッパー
│   ├── WebScraperService.cs        # Webスクレイピング統合（Reddit/X/Instagram/汎用HTTP）
│   ├── PlaywrightScraperService.cs # Playwrightブラウザ自動化
│   ├── ClaudeCodeSetupService.cs   # Node.js & Claude CLI セットアップ
│   └── ClaudeCodeProcessHost.cs    # Claude CLIプロセス管理
├── Models/
│   ├── AppJsonContext.cs       # System.Text.Json AOTソース生成コンテキスト
│   └── RedditModels.cs         # Redditデータ構造
└── Scripts/                    # Python スクリプト群（convert_files.py, scrape_*.py）
```

### Velopack 自動更新

- **インストール先**: `%LOCALAPPDATA%\MarkItDown.GUI\`
  - `current/` — アプリ本体（更新時に差し替え）
  - `lib/` — Python/FFmpeg/Node.js（更新で消えない）
  - `appsettings.xml` — 設定（更新で消えない）
- **更新ソース**: GitHub Releases（`1llum1n4t1s/MarkItDown.GUI`）
- **更新チェック**: アプリ起動時に `App.CheckForUpdateInBackground()` でバックグラウンド実行。更新があれば自動DL→再起動適用
- Velopackフック: update後に旧スタートアップ Run キーを掃除（1.0.72以前の移行対応）

### パス解決の注意点（AppPathHelper）

`AppPathHelper.IsVelopackEnvironment` で判定:
- Velopack環境: BaseDirectory が `\current` で終わる → `lib/` は親ディレクトリ
- 開発環境: BaseDirectory直下に `lib/` がある前提

### 外部依存（全てアプリ内 lib/ に自己完結）

- **Python 3.10+** (埋め込み版) — markitdown, playwright, httpx, instaloader等
- **FFmpeg** — 音声ファイル処理
- **Node.js v20.18.1** (オプション) — Claude Code CLI実行環境
- 初回起動時に自動DL・セットアップ。OSのPATH/レジストリは汚さない

## Code Conventions

- **言語**: 日本語のコメント・ログメッセージ（ユーザー向けメッセージは「〜なのだ」口調）
- **AOT対応**: `PublishAot=true`。`System.Text.Json` はソース生成コンテキスト(`AppJsonContext`)を使用。`[GeneratedRegex]` でコンパイル時正規表現
- **コンパイル済みバインディング**: `AvaloniaUseCompiledBindingsByDefault=true`
- **非同期**: I/O操作はすべてasync。ライブラリ側は `ConfigureAwait(false)`。UIスレッドへは `Dispatcher.UIThread.Post()`
- **ログ**: `Logger.Log(message, LogLevel)` で統一。Releaseビルドは **Warning以上のみ** ファイル出力（Info/Debugは無視される）
- **リソース管理**: `IDisposable` パターン。二重Dispose防止。`App.axaml.cs` でViewModel破棄

## CI/CD

- **ビルド**: `main` ブランチへのpush → `dotnet-build.yml`（ビルド確認のみ）
- **リリース**: `releases` or `release/**` ブランチへのpush → `velopack-release.yml`
  - バージョン: `release/X.Y.Z` → X.Y.Z、それ以外 → `1.0.{run_number}`
  - `vpk pack` → `vpk upload github` でGitHub Releasesにアップロード
