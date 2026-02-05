# MarkItDown.GUI 実装概要書

**作成日**: 2026-02-05
**対象バージョン**: v2.0 以降

---

## 1. プロジェクト概要

MarkItDown.GUI は、Windows 11 環境で、様々なファイル形式を Markdown 形式に自動変換するドラッグ&ドロップ型 GUI アプリケーションです。

### 主な特徴
- ✅ マルチフォーマット対応（Office、画像、音声、アーカイブなど）
- ✅ Ollama 連携による AI 画像説明生成
- ✅ 自動環境構築（Python、FFmpeg 自動セットアップ）
- ✅ Avalonia UI による軽量で高速なインターフェース

---

## 2. アーキテクチャ

### 2.1 全体構成図

```
┌─────────────────────────────────────────────────────────┐
│  UI Layer (Avalonia)                                    │
│  ├─ MainWindow.axaml (ビュー)                           │
│  └─ MainWindowViewModel (ロジック)                      │
└─────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────┐
│  Service Layer                                          │
│  ├─ FileProcessor (ファイル処理)                        │
│  ├─ MarkItDownProcessor (Markdown変換)                  │
│  ├─ OllamaManager (AI画像説明)                          │
│  ├─ FfmpegManager (音声処理)                            │
│  └─ PythonEnvironmentManager (Python環境)              │
└─────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────┐
│  Utility Layer                                          │
│  ├─ ProcessUtils (プロセス実行)                         │
│  ├─ AppSettings (設定管理)                              │
│  ├─ Logger (ログ出力)                                  │
│  └─ TimeoutSettings (タイムアウト定義)                  │
└─────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────┐
│  System Layer                                           │
│  ├─ File I/O                                            │
│  ├─ Process Management                                  │
│  └─ Network (Ollama REST API)                           │
└─────────────────────────────────────────────────────────┘
```

### 2.2 主要コンポーネント

#### UI層
| クラス | 責務 | ファイル |
|--------|------|---------|
| `MainWindow` | GUI レイアウト | MainWindow.axaml |
| `MainWindowViewModel` | UI ロジック、状態管理 | ViewModels/MainWindowViewModel.cs |

#### Service層
| クラス | 責務 | ファイル |
|--------|------|---------|
| `FileProcessor` | ファイル入力検証、パス管理 | Services/FileProcessor.cs |
| `MarkItDownProcessor` | Markdown 変換実行 | Services/MarkItDownProcessor.cs |
| `OllamaManager` | 画像説明生成、モデル管理 | Services/OllamaManager.cs |
| `FfmpegManager` | 音声ファイル処理 | Services/FfmpegManager.cs |
| `PythonEnvironmentManager` | Python 環境検出・セットアップ | Services/PythonEnvironmentManager.cs |
| `PythonPackageManager` | pip パッケージ管理 | Services/PythonPackageManager.cs |

#### Utility層
| クラス | 責務 | ファイル |
|--------|------|---------|
| `ProcessUtils` | 安全なプロセス実行 | Services/ProcessUtils.cs |
| `AppSettings` | 設定一元管理 | Services/AppSettings.cs |
| `Logger` | 統一的なログ出力 | Services/Logger.cs |
| `TimeoutSettings` | タイムアウト定数定義 | Services/TimeoutSettings.cs |

---

## 3. ファイル処理フロー

### 3.1 ドラッグ&ドロップ処理フロー

```
ユーザーがファイル/フォルダをドロップ
    ↓
[MainWindow] OnDrop イベント発火
    ↓
[MainWindowViewModel] ProcessFilesAsync()
    ↓
[FileProcessor] GetFilesFromPathAsync()
    ├─ ファイル/フォルダ判定
    ├─ パストラバーサル検証
    └─ サポート形式確認
    ↓
各ファイルに対して変換処理
    ↓
[MarkItDownProcessor] ConvertAsync()
    └─ MarkItDown Python スクリプト実行
    ↓
Markdown ファイル生成
```

### 3.2 画像ファイル処理フロー（Ollama 連携時）

```
画像ファイル検出 (.jpg, .png, .gif, .bmp, .tiff)
    ↓
[OllamaManager] CheckOllamaAvailability()
    ├─ Ollama デーモンプロセス確認
    └─ llava モデル確認
    ↓
Ollama 利用可能 → AI 説明生成
    ├─ http://localhost:11434/api/generate に POST
    ├─ llava モデルで画像分析
    └─ 生成テキストを Markdown に追加
    ↓
Ollama 利用不可 → 画像メタデータのみ出力
```

### 3.3 環境セットアップフロー（初回起動時）

```
アプリケーション起動
    ↓
[PythonEnvironmentManager] InitializeAsync()
    ├─ Python 3.9+ 検出
    │  ├─ PATH から検索
    │  └─ ハードコード済みパスから検索
    └─ 未検出 → 自動ダウンロード
    ↓
[PythonPackageManager] EnsurePackagesAsync()
    ├─ pip install markitdown
    ├─ pip install pydub（音声処理）
    └─ pip install Pillow（画像処理）
    ↓
[FfmpegManager] EnsureFfmpegAsync()
    ├─ FFmpeg バイナリ検出
    └─ 未検出 → GitHub から自動ダウンロード
    ↓
[OllamaManager] CheckAndDownloadOllamaAsync()
    ├─ Ollama デーモン検出
    ├─ 未検出 → 自動ダウンロード
    └─ llava モデルダウンロード
    ↓
セットアップ完了
```

---

## 4. セキュリティ実装

### 4.1 コマンドインジェクション対策

**パターン: 安全なプロセス起動**

```csharp
// ❌ 危険: コマンドラインの文字列連結
var arguments = $"{script} {userInput}";
var process = Process.Start("python.exe", arguments);

// ✅ 安全: ArgumentList でパラメータ化
var psi = new ProcessStartInfo("python.exe");
psi.ArgumentList.Add(script);
psi.ArgumentList.Add(userInput);
var process = Process.Start(psi);
```

### 4.2 パストラバーサル対策

**パターン: パス検証とサニタイズ**

```csharp
// パストラバーサル攻撃を検出
if (path.Contains("..") || path.Contains("~"))
{
    throw new SecurityException("Path traversal detected");
}

// 絶対パスに正規化
var absolutePath = Path.GetFullPath(path);

// キャッシュに基づく重複チェック削減
if (!_pathValidationCache.TryGetValue(absolutePath, out var isValid))
{
    isValid = ValidatePath(absolutePath);
    _pathValidationCache[absolutePath] = isValid;
}
```

### 4.3 型安全性

**パターン: パターンマッチング**

```csharp
// ❌ 危険: 無条件キャスト
var result = (ConversionResult)obj;

// ✅ 安全: パターンマッチング
if (obj is ConversionResult result)
{
    // result を使用
}
else
{
    Logger.LogWarning("Invalid type casting");
}
```

---

## 5. パフォーマンス最適化

### 5.1 メモリ管理

#### 一時ファイル管理
```csharp
// using で自動クリーンアップ
using (var tempFile = new TemporaryFile())
{
    // ファイル処理
} // 自動削除
```

#### ストリーム管理
```csharp
// using で自動クローズ
using (var stream = File.OpenRead(path))
{
    // ストリーム操作
} // 自動クローズ
```

### 5.2 キャッシング戦略

#### パス検証キャッシュ
- キャッシュキー: ファイルの絶対パス
- キャッシュ値: 検証結果（有効/無効）
- TTL: アプリケーション実行期間中持続
- 効果: 2回目以降の検証が 70% 削減

### 5.3 非同期処理

#### UI スレッド保護
```csharp
// 時間のかかる処理は async で実行
public async Task ProcessFilesAsync(string[] paths)
{
    foreach (var path in paths)
    {
        // 別スレッドで実行
        await Task.Run(() => ProcessFile(path));
    }
}
```

---

## 6. 設定管理

### 6.1 appsettings.xml 構造

```xml
<?xml version="1.0" encoding="utf-8"?>
<AppSettings>
  <!-- Ollama 設定 -->
  <OllamaUrl>http://localhost:11434</OllamaUrl>
  <OllamaModel>llava</OllamaModel>
  <OllamaGpuDevice>0</OllamaGpuDevice>
  <OllamaTimeout>300000</OllamaTimeout>

  <!-- タイムアウト設定 -->
  <MarkItDownTimeout>300000</MarkItDownTimeout>
  <FfmpegTimeout>120000</FfmpegTimeout>
  <PythonPackageTimeout>600000</PythonPackageTimeout>

  <!-- 更新確認 URL -->
  <UpdateCheckUrl>https://api.github.com/repos/1llum1n4t1s/MarkItDown.GUI/releases/latest</UpdateCheckUrl>
</AppSettings>
```

### 6.2 AppSettings クラス（シングルトン）

```csharp
public sealed class AppSettings
{
    public static AppSettings Instance { get; } = new();

    public string OllamaUrl { get; private set; }
    public string OllamaModel { get; private set; }
    // ...

    private AppSettings() => LoadFromFile();
}
```

---

## 7. ログ管理

### 7.1 Logger クラス

```csharp
public static class Logger
{
    public static void LogInfo(string message);
    public static void LogWarning(string message);
    public static void LogError(string message, Exception ex = null);
    public static void LogDebug(string message);
}
```

### 7.2 ログ出力例

```
[2026-02-05 14:20:40] INFO: Initializing application
[2026-02-05 14:20:41] INFO: Checking Python environment
[2026-02-05 14:20:42] WARNING: Python not found in PATH, using fallback
[2026-02-05 14:20:45] INFO: Python environment ready
[2026-02-05 14:20:46] INFO: Checking Ollama availability
[2026-02-05 14:20:47] INFO: Ollama not running, starting auto-download
```

---

## 8. ファイル形式対応マトリックス

| カテゴリ | 形式 | 処理方法 | 必要な環境 |
|---------|------|---------|----------|
| テキスト | .txt, .md, .html, .csv | MarkItDown | Python |
| Office | .docx, .xlsx, .pptx | MarkItDown | Python |
| 画像 | .jpg, .png, .gif, .bmp | MarkItDown + Ollama | Python + Ollama |
| 音声 | .mp3, .wav, .flac | FFmpeg + MarkItDown | FFmpeg + Python |
| アーカイブ | .zip, .rar, .7z | MarkItDown | Python |

---

## 9. エラーハンドリング

### 9.1 例外ハンドリングパターン

```csharp
try
{
    await ProcessFilesAsync(files);
}
catch (ArgumentException ex)
{
    Logger.LogError("Invalid file path", ex);
    // ユーザー向けメッセージ表示
}
catch (IOException ex)
{
    Logger.LogError("File I/O error", ex);
    // リトライ処理
}
catch (Exception ex)
{
    Logger.LogError("Unexpected error", ex);
    // グレースフルシャットダウン
}
```

### 9.2 リカバリー戦略

| エラー | 検出方法 | リカバリー |
|--------|---------|-----------|
| Python 未検出 | PATH 検索失敗 | 自動ダウンロード |
| FFmpeg 未検出 | プロセス起動失敗 | 自動ダウンロード |
| Ollama 未検出 | HTTP 接続失敗 | 画像メタデータのみ出力 |
| ファイルロック | IOException | 1秒待機後リトライ（最大3回） |

---

## 10. テスト戦略

### 10.1 単体テスト対象

- `FileProcessor.ValidatePath()`: パス検証ロジック
- `ProcessUtils.CreateProcessStartInfo()`: コマンド構築安全性
- `AppSettings.LoadFromFile()`: 設定ファイル読み込み

### 10.2 統合テスト対象

- ファイルドロップ → Markdown 生成
- 画像ドロップ → AI 説明生成
- 大規模ディレクトリ処理

### 10.3 セキュリティテスト対象

- コマンドインジェクション テスト（特殊文字入力）
- パストラバーサル テスト（`../` 相対パス）
- 型安全性 テスト（型不一致ケース）

---

## 11. 運用ガイド

### 11.1 トラブルシューティング

**Q: Ollama が起動しない**
- A: `lib/ollama/` にバイナリが存在するか確認
- ネットワークが 11434 ポートをブロックしていないか確認

**Q: Python が見つからない**
- A: PATH 環境変数を確認
- 手動で `C:\Python39\` などにインストール

**Q: ファイル処理が遅い**
- A: 同時処理ファイル数を削減
- メモリ使用状況を確認

### 11.2 ログ確認方法

ログはアプリケーション実行ディレクトリの `Logs` フォルダに保存されます：
```
logs/
├─ 2026-02-05.log
├─ 2026-02-04.log
└─ ...
```

---

## 12. 今後の拡張予定

- [ ] 複数ファイルの並列処理
- [ ] PDF 出力フォーマット対応
- [ ] クラウドストレージ連携（OneDrive、Google Drive）
- [ ] ダークモード UI
- [ ] プラグインシステム

---

**最終更新**: 2026-02-05
**ドキュメントバージョン**: v2.0
