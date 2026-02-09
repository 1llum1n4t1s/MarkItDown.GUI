import os
import sys
import json
import traceback
from datetime import datetime
import asyncio

try:
    from openai import OpenAI
except ImportError:
    OpenAI = None

# グローバル定数（毎回の再生成を回避）
# .md / .json は出力形式と同一のため、フォルダスキャン時に自分の出力ファイルを再変換してしまうので除外
SUPPORTED_EXTENSIONS = {
    '.txt', '.html', '.htm', '.csv', '.xml',
    '.docx', '.doc', '.xlsx', '.xls', '.pptx', '.ppt',
    '.pdf',
    '.jpg', '.jpeg', '.png', '.gif', '.bmp', '.tiff', '.tif', '.webp',
    '.mp3', '.wav', '.flac', '.aac', '.ogg',
    '.zip', '.rar', '.7z', '.tar', '.gz'
}

IMAGE_EXTENSIONS = {'.jpg', '.jpeg', '.png', '.gif', '.bmp', '.tiff', '.tif', '.webp'}

# LLM分析・整形時の閾値定数
MIN_LEN_FOR_SUMMARY_CHECK = 200  # 要約結果の長さチェックを行う最小の元テキスト長
MIN_SUMMARY_RATIO = 0.05  # 要約結果が元テキストの何%未満なら棄却するか


def log_message(message):
    print(message, flush=True)

def _write_output_file(content, base_name, suffix, timestamp, directory):
    """ヘルパー関数: ファイルにコンテンツを書き込む。書き込んだファイル名を返す。"""
    if not content or not content.strip():
        log_message(f'{suffix} の内容が空のため、ファイル出力をスキップします。')
        return None

    filename = f'{base_name}_{suffix}_{timestamp}.md'
    output_path = os.path.join(directory, filename)

    with open(output_path, 'w', encoding='utf-8') as f:
        f.write(content)

    log_message(f'{suffix}出力完了: {output_path}')
    return filename

def create_openai_client(ollama_url):
    """OllamaのOpenAI互換クライアントを作成する。"""
    if OpenAI is None:
        log_message('openaiパッケージが利用できません。')
        return None
    try:
        client = OpenAI(
            base_url=f"{ollama_url}/v1",
            api_key="ollama"
        )
        log_message(f'OpenAIクライアント作成: {ollama_url}/v1')
        return client
    except Exception as e:
        log_message(f'OpenAIクライアント作成に失敗: {e}')
        return None

LLM_PROMPT = "この画像について詳しく説明してください。画像の内容、オブジェクト、色、雰囲気などを含めて説明してください。日本語で回答してください。"

def create_markitdown_instance(ollama_client, ollama_model):
    """MarkItDownインスタンスを作成する。Ollama設定があればネイティブLLM統合を使用する。"""
    import markitdown

    if ollama_client and ollama_model:
        try:
            md = markitdown.MarkItDown(
                llm_client=ollama_client,
                llm_model=ollama_model,
                llm_prompt=LLM_PROMPT,
            )
            log_message(f'MarkItDownインスタンス作成（Ollama統合有効、モデル: {ollama_model}）')
            return md
        except Exception as e:
            log_message(f'Ollama統合の初期化に失敗: {e}。LLM統合なしで続行します。')

    md = markitdown.MarkItDown()
    log_message('MarkItDownインスタンス作成（LLM統合なし）')
    return md

def summarize_markdown_with_llm(ollama_client, ollama_model, raw_markdown, file_name):
    """Ollamaを使ってMarkdownテキストを分析・統計まとめする。"""
    if not ollama_client or not ollama_model:
        return None

    try:
        log_message(f'LLMでMarkdown分析開始: {file_name}')

        response = ollama_client.chat.completions.create(
            model=ollama_model,
            messages=[
                {
                    "role": "system",
                    "content": (
                        "あなたは文書分析の専門家です。\n"
                        "入力されたMarkdownテキストを分析し、統計情報と構造化されたまとめを作成してください。\n\n"
                        "【出力構成（この順序で出力すること）】\n"
                        "1. 概要: 文書全体の目的・テーマを2〜3文で説明\n"
                        "2. 統計情報: 文字数、段落数、見出し数、リンク数、画像数、表の数など該当するものを表形式で列挙\n"
                        "3. 文書構造: 見出し階層をツリー形式で表示\n"
                        "4. 主要トピック: 文書内の主要なトピックを箇条書きで列挙し、各トピックの要点を1〜2文で説明\n"
                        "5. キーワード・固有名詞: 文書内で重要なキーワード、固有名詞、数値データを列挙\n"
                        "6. 結論・要点: 文書の結論や最も重要なポイントをまとめる\n\n"
                        "【ルール】\n"
                        "- 元のテキストの言語をそのまま使用する（日本語→日本語、英語→英語）\n"
                        "- 出力はMarkdown形式で整形する\n"
                        "- 情報を省略せず、網羅的に分析する\n"
                        "- 分析結果のMarkdownだけを出力する（説明文や前置きは不要）"
                    )
                },
                {
                    "role": "user",
                    "content": f"以下のMarkdownテキストを分析し、統計情報と構造化されたまとめを作成してください。\n\n{raw_markdown}"
                }
            ],
            timeout=300
        )

        summary = response.choices[0].message.content
        if summary and len(summary.strip()) > 0:
            summary_len = len(summary)
            original_len = len(raw_markdown)
            ratio = summary_len / original_len if original_len > 0 else 1.0
            log_message(f'LLM分析完了: {original_len}文字 → {summary_len}文字 ({ratio:.0%})')

            # 分析結果が元テキストの5%未満に短縮された場合は短すぎと判断して棄却
            if original_len > MIN_LEN_FOR_SUMMARY_CHECK and ratio < MIN_SUMMARY_RATIO:
                log_message(f'LLM分析結果が短すぎる（{ratio:.0%}）ため、棄却します。')
                return None

            return summary
        else:
            log_message('LLMまとめの結果が空でした。')
            return None

    except Exception as e:
        log_message(f'LLMまとめエラー: {e}')
        return None


def refine_markdown_with_llm(ollama_client, ollama_model, raw_markdown, file_name):
    """Ollamaを使って崩れたMarkdownテキストを整形する。"""
    if not ollama_client or not ollama_model:
        return raw_markdown

    try:
        log_message(f'LLMでMarkdown整形開始: {file_name}')

        response = ollama_client.chat.completions.create(
            model=ollama_model,
            messages=[
                {
                    "role": "system",
                    "content": (
                        "あなたはMarkdown整形アシスタントです。\n"
                        "入力されたMarkdownテキストの書式だけを整えてください。\n\n"
                        "【絶対に守るルール】\n"
                        "- 元のテキストの言語をそのまま維持する（日本語は日本語、英語は英語のまま）\n"
                        "- 元のテキストの内容を絶対に要約・翻訳・削除・追加・言い換えしない\n"
                        "- 元のテキストの文字数をほぼ維持する（大幅に短くしない）\n"
                        "- 表（テーブル）データはそのまま維持する\n\n"
                        "【整形で行うこと】\n"
                        "- 不要な連続空行を1行にまとめる\n"
                        "- 壊れたMarkdown記法（閉じ忘れ、不正なリスト等）を修正する\n"
                        "- 見出しレベル（#, ##, ###）の一貫性を保つ\n"
                        "- 整形結果のMarkdownだけを出力する（説明文や前置きは不要）"
                    )
                },
                {
                    "role": "user",
                    "content": f"以下のMarkdownテキストの書式を整えてください。内容は変更せず、そのまま維持してください。\n\n{raw_markdown}"
                }
            ],
            timeout=300
        )

        refined = response.choices[0].message.content
        if refined and len(refined.strip()) > 0:
            refined_len = len(refined)
            original_len = len(raw_markdown)
            log_message(f'LLM整形完了: {original_len}文字 → {refined_len}文字')

            # 整形結果が元テキストの50%未満に短縮された場合は要約と判断して棄却
            if original_len > 100 and refined_len < original_len * 0.5:
                log_message(f'LLM整形結果が大幅に短縮されている（{refined_len}/{original_len} = {refined_len/original_len:.0%}）ため、要約と判断して元のテキストを使用します。')
                return raw_markdown

            return refined
        else:
            log_message('LLM整形の結果が空でした。元のテキストを使用します。')
            return raw_markdown

    except Exception as e:
        log_message(f'LLM整形エラー: {e}。元のテキストを使用します。')
        return raw_markdown

try:
    log_message('Pythonスクリプト開始')

    # Get application directory
    app_dir = os.path.dirname(os.path.abspath(__file__))
    log_message('Application directory: ' + app_dir)

    # Ollama設定を環境変数から取得
    ollama_url = os.environ.get('OLLAMA_URL')
    ollama_model = os.environ.get('OLLAMA_MODEL')

    if ollama_url and ollama_model:
        log_message(f'Ollama設定が検出されました: {ollama_url}, モデル: {ollama_model}')
    else:
        log_message('Ollama設定が見つかりません。画像説明機能は無効です。')

    async def process_file_async(md, file_path, ollama_client=None, ollama_model=None):
        """ファイルをMarkItDownで変換する。PDF等の構造化ドキュメントはOllamaで整形する。"""
        try:
            file_name = os.path.basename(file_path)
            file_dir = os.path.dirname(file_path)
            name_without_ext = os.path.splitext(file_name)[0]
            file_ext = os.path.splitext(file_path)[1].lower()

            log_message(f'ファイル処理開始: {file_path}')
            log_message(f'ファイル名: {file_name}')

            # Convert file using MarkItDown (LLM統合が有効ならば画像説明も自動生成)
            try:
                loop = asyncio.get_running_loop()
                result = await loop.run_in_executor(None, md.convert, file_path)
                markdown_content = result.text_content
                log_message(f'変換完了、コンテンツ長: {len(markdown_content)}文字')
            except Exception as convert_error:
                log_message(f'ファイル変換エラー: {file_path} - {str(convert_error)}')
                traceback.print_exc()
                raise

            # 変換結果が空の場合、ファイル情報を追加
            if not markdown_content or len(markdown_content.strip()) == 0:
                log_message(f'警告: 変換結果が空です。ファイル情報を追加します。')

                if file_ext in IMAGE_EXTENSIONS:
                    file_size = os.path.getsize(file_path)
                    markdown_content = f"# {file_name}\n\n"
                    markdown_content += f"画像ファイル: `{file_name}`\n\n"
                    markdown_content += f"- ファイルパス: `{file_path}`\n"
                    markdown_content += f"- ファイルサイズ: {file_size:,} バイト\n"
                    markdown_content += f"- 拡張子: {file_ext}\n\n"
                    markdown_content += f"![{file_name}]({file_path})\n\n"
                    markdown_content += "注: この画像にはテキスト情報が含まれていないか、OCR処理でテキストが検出されませんでした。\n"
                    markdown_content += "画像の内容を説明するには、Ollama (gemma3 モデル推奨) を使用してください。\n"
                    log_message(f'画像ファイル情報を追加しました')
                else:
                    markdown_content = f"# {file_name}\n\n変換結果が空でした。\n"
            else:
                log_message(f'変換内容の先頭100文字: {markdown_content[:100]}')

            # タイムスタンプを生成（全出力ファイルで共通）
            timestamp = datetime.now().strftime('%Y%m%d%H%M%S')

            # 1. 元データ（生データ）を出力
            origin_filename = _write_output_file(
                markdown_content, name_without_ext, '元データ', timestamp, file_dir
            )

            # 2. 整形済・まとめ済（Ollama利用可能時のみ）
            if (markdown_content and len(markdown_content.strip()) > 0
                    and ollama_client and ollama_model):
                formatted_content = await loop.run_in_executor(
                    None, refine_markdown_with_llm,
                    ollama_client, ollama_model, markdown_content, file_name
                )
                _write_output_file(
                    formatted_content, name_without_ext, '整形済', timestamp, file_dir
                )

                summary_content = await loop.run_in_executor(
                    None, summarize_markdown_with_llm,
                    ollama_client, ollama_model, markdown_content, file_name
                )
                _write_output_file(
                    summary_content, name_without_ext, 'まとめ済', timestamp, file_dir
                )

            log_message(f'ファイル出力完了: {file_name}')
            return f'変換完了: {file_name} → {origin_filename}'

        except Exception as e:
            error_type = type(e).__name__
            error_msg = str(e)

            # サポートされていないファイル形式の場合
            if 'UnsupportedFormatException' in error_type or 'not supported' in error_msg.lower():
                log_message(f'サポートされていないファイル形式: {file_path}')
                file_ext = os.path.splitext(file_path)[1].lower()
                log_message(f'このファイル形式は MarkItDown でサポートされていません: {file_ext}')

                file_size = os.path.getsize(file_path)
                markdown_content = f"# {file_name}\n\n"
                markdown_content += f"**サポートされていないファイル形式**\n\n"
                markdown_content += f"- ファイルパス: `{file_path}`\n"
                markdown_content += f"- ファイルサイズ: {file_size:,} バイト\n"
                markdown_content += f"- 拡張子: {file_ext}\n\n"
                markdown_content += f"このファイル形式（{file_ext}）は MarkItDown でサポートされていません。\n\n"
                markdown_content += "サポートされているファイル形式:\n"
                markdown_content += "- テキスト: .txt, .md, .html, .csv, .json, .xml\n"
                markdown_content += "- Office: .docx, .xlsx, .pptx\n"
                markdown_content += "- 画像: .jpg, .png, .gif, .bmp, .tiff\n"
                markdown_content += "- 音声: .mp3, .wav, .flac\n"
                markdown_content += "- PDF: .pdf\n"

                timestamp = datetime.now().strftime('%Y%m%d%H%M%S')
                output_filename = f'{name_without_ext}_{timestamp}.md'
                output_path = os.path.join(file_dir, output_filename)

                with open(output_path, 'w', encoding='utf-8') as f:
                    f.write(markdown_content)

                return f'サポート外: {file_name}'
            else:
                log_message(f'変換エラー: {file_path} - {str(e)}')
                traceback.print_exc()
                return f'変換エラー: {file_path} - {str(e)}'

    async def convert_files_async(file_paths, folder_paths):
        results = []

        log_message(f'Start convert_files: {len(file_paths)} files, {len(folder_paths)} folders')

        try:
            # Import MarkItDown library once at the beginning
            log_message('Importing MarkItDown library...')
            import markitdown
            log_message('MarkItDown library imported successfully')

            # OpenAIクライアントを作成（MarkItDownとLLM整形の両方で再利用）
            ollama_client = None
            if ollama_url and ollama_model:
                ollama_client = create_openai_client(ollama_url)

            # Create a single MarkItDown instance to reuse (Ollama統合含む)
            log_message('MarkItDownインスタンスを作成中...')
            try:
                md = create_markitdown_instance(ollama_client, ollama_model)
            except Exception as e:
                log_message(f'MarkItDownインスタンス作成エラー: {e}')
                raise

            # Process files with concurrency control (max 3 concurrent tasks)
            if file_paths:
                log_message(f'ファイル処理開始（最大3並列処理）')
                for i in range(0, len(file_paths), 3):
                    batch = file_paths[i:i+3]
                    batch_tasks = [process_file_async(md, file_path, ollama_client, ollama_model) for file_path in batch]
                    batch_results = await asyncio.gather(*batch_tasks, return_exceptions=True)
                    for result in batch_results:
                        if isinstance(result, Exception):
                            log_message(f'バッチ処理エラー: {result}')
                            results.append(f'処理エラー: {str(result)}')
                        else:
                            results.append(result)

            # フォルダの処理（process_file_asyncを再利用）
            for folder_path in folder_paths:
                log_message(f'フォルダ処理開始: {folder_path}')
                if os.path.exists(folder_path):
                    try:
                        folder_name = os.path.basename(folder_path)

                        # フォルダ内のサポート対象ファイルを収集
                        folder_file_paths = []
                        for root, dirs, files in os.walk(folder_path, followlinks=False):
                            for file in files:
                                file_ext = os.path.splitext(file)[1].lower()
                                if file_ext in SUPPORTED_EXTENSIONS:
                                    folder_file_paths.append(os.path.join(root, file))
                                else:
                                    log_message(f'サポートされていないファイル形式: {file}')

                        log_message(f'フォルダ内対象ファイル数: {len(folder_file_paths)}')

                        # バッチ並列処理（最大3並列）
                        converted_count = 0
                        for i in range(0, len(folder_file_paths), 3):
                            batch = folder_file_paths[i:i+3]
                            batch_tasks = [process_file_async(md, fp, ollama_client, ollama_model) for fp in batch]
                            batch_results = await asyncio.gather(*batch_tasks, return_exceptions=True)
                            for r in batch_results:
                                if isinstance(r, Exception):
                                    log_message(f'フォルダ内ファイル処理エラー: {r}')
                                else:
                                    converted_count += 1

                        results.append(f'フォルダ処理完了: {folder_name} (変換: {converted_count}個)')

                    except Exception as e:
                        results.append(f'フォルダ処理エラー: {folder_path} - {str(e)}')
                else:
                    log_message(f'フォルダが存在しません: {folder_path}')

            return results

        except ImportError as e:
            log_message(f'MarkItDownライブラリのインポートに失敗: {e}')
            log_message('MarkItDownライブラリが取得できませんでした、アプリを終了します。')
            results.append('MarkItDownライブラリが取得できませんでした、アプリを終了します。')
            return results

    # コマンドライン引数からJSONファイルパスを取得
    if len(sys.argv) < 3:
        log_message('エラー: ファイルパスとフォルダパスのJSONファイルパスが必要です')
        sys.exit(1)

    file_paths_json_file = sys.argv[1]
    folder_paths_json_file = sys.argv[2]

    log_message(f'受け取ったファイルパスJSONファイル: {file_paths_json_file}')
    log_message(f'受け取ったフォルダパスJSONファイル: {folder_paths_json_file}')

    # JSONファイルからデータを読み取る
    try:
        with open(file_paths_json_file, 'r', encoding='utf-8') as f:
            file_paths_json = f.read()
        with open(folder_paths_json_file, 'r', encoding='utf-8') as f:
            folder_paths_json = f.read()

        log_message(f'読み取ったファイルパスJSON: {file_paths_json}')
        log_message(f'読み取ったフォルダパスJSON: {folder_paths_json}')

        file_paths = json.loads(file_paths_json)
        folder_paths = json.loads(folder_paths_json)

        # 型チェック
        if not isinstance(file_paths, list):
            log_message(f'エラー: ファイルパスがリスト型ではありません: {type(file_paths)}')
            sys.exit(1)
        if not isinstance(folder_paths, list):
            log_message(f'エラー: フォルダパスがリスト型ではありません: {type(folder_paths)}')
            sys.exit(1)

        log_message('JSONデータのパースに成功しました')
    except FileNotFoundError as e:
        log_message(f'JSONファイルが見つかりません: {e}')
        sys.exit(1)
    except json.JSONDecodeError as e:
        log_message(f'JSONデコードエラー: {e}')
        log_message(f'ファイルパスJSON長: {len(file_paths_json)}')
        log_message(f'フォルダパスJSON長: {len(folder_paths_json)}')
        sys.exit(1)

    log_message(f'処理対象ファイル数: {len(file_paths)}')
    log_message(f'処理対象フォルダ数: {len(folder_paths)}')

    # ファイルとフォルダを非同期で変換
    results = asyncio.run(convert_files_async(file_paths, folder_paths))

    # 結果を出力
    for result in results:
        log_message(result)

    log_message('Pythonスクリプト完了')
    
except Exception as e:
    log_message('Pythonスクリプト実行中にエラー: ' + str(e))
    log_message('スタックトレース: ' + traceback.format_exc())
    sys.exit(1) 