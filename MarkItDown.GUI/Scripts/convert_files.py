import os
import sys
import json
import traceback
from datetime import datetime
import asyncio

# グローバル定数（毎回の再生成を回避）
SUPPORTED_EXTENSIONS = {
    '.txt', '.md', '.html', '.htm', '.csv', '.json', '.xml',
    '.docx', '.doc', '.xlsx', '.xls', '.pptx', '.ppt',
    '.pdf',
    '.jpg', '.jpeg', '.png', '.gif', '.bmp', '.tiff', '.tif', '.webp',
    '.mp3', '.wav', '.flac', '.aac', '.ogg',
    '.zip', '.rar', '.7z', '.tar', '.gz'
}

IMAGE_EXTENSIONS = {'.jpg', '.jpeg', '.png', '.gif', '.bmp', '.tiff', '.tif', '.webp'}

# LLMによるMarkdown整形の対象となるファイル形式
LLM_REFINABLE_EXTENSIONS = {'.pdf', '.pptx', '.ppt', '.docx', '.doc'}

def log_message(message):
    print(message, flush=True)

def create_openai_client(ollama_url):
    """OllamaのOpenAI互換クライアントを作成する。"""
    try:
        from openai import OpenAI
        client = OpenAI(
            base_url=f"{ollama_url}/v1",
            api_key="ollama"
        )
        log_message(f'OpenAIクライアント作成: {ollama_url}/v1')
        return client
    except ImportError:
        log_message('openaiパッケージが利用できません。')
        return None
    except Exception as e:
        log_message(f'OpenAIクライアント作成に失敗: {e}')
        return None

def create_markitdown_instance(ollama_client, ollama_model):
    """MarkItDownインスタンスを作成する。Ollama設定があればネイティブLLM統合を使用する。"""
    import markitdown

    if ollama_client and ollama_model:
        try:
            md = markitdown.MarkItDown(
                llm_client=ollama_client,
                llm_model=ollama_model,
                llm_prompt="この画像について詳しく説明してください。画像の内容、オブジェクト、色、雰囲気などを含めて説明してください。日本語で回答してください。"
            )
            log_message(f'MarkItDownインスタンス作成（Ollama統合有効、モデル: {ollama_model}）')
            return md
        except Exception as e:
            log_message(f'Ollama統合の初期化に失敗: {e}。LLM統合なしで続行します。')

    md = markitdown.MarkItDown()
    log_message('MarkItDownインスタンス作成（LLM統合なし）')
    return md

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
                        "あなたはドキュメント整形の専門家です。"
                        "入力されたテキストは、PDFなどから抽出された構造が崩れたテキストです。"
                        "以下のルールに従って、きれいなMarkdown形式に整形してください。\n\n"
                        "ルール:\n"
                        "- 表形式のデータはMarkdownテーブル（| col1 | col2 |）に変換する\n"
                        "- ラベルと値のペア（例: 発注番号 XXX）は定義リストまたは表にまとめる\n"
                        "- 見出しには適切なMarkdownヘッダー（#, ##）を付ける\n"
                        "- 元のテキストの情報を勝手に追加・削除・変更しない\n"
                        "- 整形結果のMarkdownだけを出力する（説明文は不要）"
                    )
                },
                {
                    "role": "user",
                    "content": raw_markdown
                }
            ],
            timeout=300
        )

        refined = response.choices[0].message.content
        if refined and len(refined.strip()) > 0:
            log_message(f'LLM整形完了: {len(raw_markdown)}文字 → {len(refined)}文字')
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

            # PDF等の構造化ドキュメントの場合、OllamaでMarkdown整形を行う
            if (markdown_content and len(markdown_content.strip()) > 0
                    and file_ext in LLM_REFINABLE_EXTENSIONS
                    and ollama_client and ollama_model):
                markdown_content = await loop.run_in_executor(
                    None, refine_markdown_with_llm,
                    ollama_client, ollama_model, markdown_content, file_name
                )

            # 変換結果が空の場合、ファイル情報を追加
            if not markdown_content or len(markdown_content.strip()) == 0:
                log_message(f'警告: 変換結果が空です。ファイル情報を追加します。')

                file_ext = os.path.splitext(file_path)[1].lower()

                if file_ext in IMAGE_EXTENSIONS:
                    file_size = os.path.getsize(file_path)
                    markdown_content = f"# {file_name}\n\n"
                    markdown_content += f"画像ファイル: `{file_name}`\n\n"
                    markdown_content += f"- ファイルパス: `{file_path}`\n"
                    markdown_content += f"- ファイルサイズ: {file_size:,} バイト\n"
                    markdown_content += f"- 拡張子: {file_ext}\n\n"
                    markdown_content += f"![{file_name}]({file_path})\n\n"
                    markdown_content += "注: この画像にはテキスト情報が含まれていないか、OCR処理でテキストが検出されませんでした。\n"
                    markdown_content += "画像の内容を説明するには、Ollama (llava モデル推奨) を使用してください。\n"
                    log_message(f'画像ファイル情報を追加しました')
                else:
                    markdown_content = f"# {file_name}\n\n変換結果が空でした。\n"
            else:
                log_message(f'変換内容の先頭100文字: {markdown_content[:100]}')

            # タイムスタンプを生成
            timestamp = datetime.now().strftime('%Y%m%d%H%M%S')
            output_filename = f'{name_without_ext}_{timestamp}.md'
            output_path = os.path.join(file_dir, output_filename)
            log_message(f'出力パス: {output_path}')

            with open(output_path, 'w', encoding='utf-8') as f:
                f.write(markdown_content)

            log_message(f'ファイル出力完了: {output_path}')
            return f'変換完了: {file_name} → {output_filename}'

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