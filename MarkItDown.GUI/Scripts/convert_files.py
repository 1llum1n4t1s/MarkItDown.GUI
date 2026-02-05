import os
import sys
import json
import traceback
from datetime import datetime
import base64
import asyncio
import urllib.request
import urllib.error

# グローバル定数（毎回の再生成を回避）
SUPPORTED_EXTENSIONS = {
    '.txt', '.md', '.html', '.htm', '.csv', '.json', '.xml',
    '.docx', '.doc', '.xlsx', '.xls', '.pptx', '.ppt',
    '.jpg', '.jpeg', '.png', '.gif', '.bmp', '.tiff', '.tif',
    '.mp3', '.wav', '.flac', '.aac', '.ogg',
    '.zip', '.rar', '.7z', '.tar', '.gz'
}

IMAGE_EXTENSIONS = {'.jpg', '.jpeg', '.png', '.gif', '.bmp', '.tiff', '.tif', '.webp'}

# 画像サイズ上限（3MB）- 大画像のBase64エンコード時間削減
MAX_IMAGE_SIZE_MB = 3

def log_message(message):
    print(message, flush=True)

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

    def generate_image_description_with_ollama_sync(image_path, ollama_url, ollama_model):
        """Ollamaを使用して画像の説明を生成する（同期版、executorで実行、サイズ上限あり）"""
        try:
            log_message(f'Ollamaで画像説明を生成中: {image_path}')

            # 画像ファイルの存在確認
            if not os.path.exists(image_path):
                log_message(f'画像ファイルが見つかりません: {image_path}')
                return None

            # 画像をBase64エンコード（サイズ上限チェック付き）
            try:
                with open(image_path, 'rb') as img_file:
                    image_bytes = img_file.read()
                    image_size_mb = len(image_bytes) / (1024 * 1024)

                    # 画像サイズ上限をチェック（メモリとエンコード時間を節約）
                    if image_size_mb > MAX_IMAGE_SIZE_MB:
                        log_message(f'スキップ: 画像サイズが上限を超えています（{image_size_mb:.2f} MB > {MAX_IMAGE_SIZE_MB} MB）')
                        return None

                    image_data = base64.b64encode(image_bytes).decode('utf-8')

                log_message(f'画像をBase64エンコードしました（元サイズ: {image_size_mb:.2f} MB, Base64: {len(image_data)}文字）')

            except Exception as e:
                log_message(f'画像の読み込みに失敗: {e}')
                return None

            # Ollama APIにリクエスト
            api_url = f"{ollama_url}/api/generate"
            payload = {
                "model": ollama_model,
                "prompt": "この画像について詳しく説明してください。画像の内容、オブジェクト、色、雰囲気などを含めて説明してください。日本語で回答してください。",
                "images": [image_data],
                "stream": False
            }

            ollama_timeout_sec = 900
            log_message(f'Ollama APIリクエスト送信: {api_url}')
            log_message(f'モデル: {ollama_model}')
            log_message(f'画像説明を生成中... (最大{ollama_timeout_sec // 60}分かかる場合があります)')

            try:
                # requestsライブラリが利用可能な場合
                import requests
                response = requests.post(api_url, json=payload, timeout=ollama_timeout_sec)
                log_message(f'レスポンス受信: ステータスコード {response.status_code}')

                if response.status_code == 200:
                    try:
                        result = response.json()
                        log_message(f'Ollama APIレスポンス: {result}')
                        description = result.get('response', '')
                        if description:
                            log_message(f'Ollamaから説明を取得しました（{len(description)}文字）')
                            return description
                        else:
                            log_message('Ollamaからの応答が空でした')
                            log_message(f'レスポンス内容: {result}')
                            return None
                    except Exception as e:
                        log_message(f'レスポンスのパースに失敗: {e}')
                        log_message(f'生のレスポンス: {response.text[:500]}')
                        return None
                else:
                    log_message(f'Ollama APIエラー: ステータスコード {response.status_code}')
                    log_message(f'エラー内容: {response.text[:500]}')
                    return None

            except ImportError:
                log_message('requestsライブラリが利用できません。画像説明生成をスキップします。')
                return None
            except Exception as e:
                # requestsライブラリのTimeoutError検出
                error_name = type(e).__name__
                if error_name in ('TimeoutError', 'Timeout', 'ConnectTimeout', 'ReadTimeout', 'HTTPError'):
                    log_message(f'Ollama APIタイムアウト（{ollama_timeout_sec}秒/{ollama_timeout_sec // 60}分）')
                    log_message('画像が大きすぎるか、モデルの処理に時間がかかっています')
                else:
                    log_message(f'Ollama APIリクエストエラー ({error_name}): {e}')
                return None

        except Exception as e:
            log_message(f'Ollama画像説明生成エラー: {e}')
            import traceback
            log_message(f'スタックトレース: {traceback.format_exc()}')
            return None

    async def process_file_with_ollama_async(md, file_path, ollama_url, ollama_model):
        """Single file conversion with async Ollama support"""
        try:
            file_name = os.path.basename(file_path)
            file_dir = os.path.dirname(file_path)
            name_without_ext = os.path.splitext(file_name)[0]

            log_message(f'ファイル処理開始: {file_path}')
            log_message(f'ファイル名: {file_name}')

            # Convert file using MarkItDown
            try:
                result = md.convert(file_path)
                markdown_content = result.text_content
                log_message(f'変換完了、コンテンツ長: {len(markdown_content)}文字')
            except Exception as convert_error:
                log_message(f'ファイル変換エラー: {file_path} - {str(convert_error)}')
                traceback.print_exc()
                raise

            # 画像ファイルの場合、変換結果が空になることがあるため、ファイル情報を追加
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

                    # Ollamaで画像説明を試みる（スレッドプール実行）
                    if ollama_url and ollama_model:
                        log_message('Ollamaで画像説明を生成します...')
                        loop = asyncio.get_running_loop()
                        description = await loop.run_in_executor(None, generate_image_description_with_ollama_sync, file_path, ollama_url, ollama_model)
                        if description:
                            markdown_content += f"## 画像の説明 (Ollama {ollama_model})\n\n"
                            markdown_content += f"{description}\n\n"
                        else:
                            markdown_content += "注: Ollamaでの画像説明生成に失敗しました。\n\n"

                    markdown_content += f"![{file_name}]({file_path})\n\n"

                    if not ollama_url or not ollama_model:
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

            # Create a single MarkItDown instance to reuse
            log_message('MarkItDownインスタンスを作成中...')
            try:
                md = markitdown.MarkItDown()
                log_message('MarkItDown instance created')
            except Exception as e:
                log_message(f'MarkItDownインスタンス作成エラー: {e}')
                raise

            # Process files with concurrency control (max 3 concurrent tasks)
            if file_paths:
                log_message(f'ファイル処理開始（最大3並列処理）')
                for i in range(0, len(file_paths), 3):
                    batch = file_paths[i:i+3]
                    batch_tasks = [process_file_with_ollama_async(md, file_path, ollama_url, ollama_model) for file_path in batch]
                    batch_results = await asyncio.gather(*batch_tasks, return_exceptions=True)
                    for result in batch_results:
                        if isinstance(result, Exception):
                            log_message(f'バッチ処理エラー: {result}')
                            results.append(f'処理エラー: {str(result)}')
                        else:
                            results.append(result)

            # フォルダの処理
            for folder_path in folder_paths:
                log_message(f'フォルダ処理開始: {folder_path}')
                if os.path.exists(folder_path):
                    try:
                        folder_name = os.path.basename(folder_path)
                        converted_count = 0
                        for root, dirs, files in os.walk(folder_path, followlinks=False):
                            for file in files:
                                file_path = os.path.join(root, file)
                                try:
                                    file_ext = os.path.splitext(file)[1].lower()

                                    log_message(f'フォルダ内ファイル: {file} (拡張子: {file_ext})')

                                    if file_ext in SUPPORTED_EXTENSIONS:
                                        log_message(f'Supported file format: {file}')
                                        try:
                                            result = md.convert(file_path)
                                            markdown_content = result.text_content

                                            if not markdown_content or len(markdown_content.strip()) == 0:
                                                log_message(f'警告: フォルダ内ファイルの変換結果が空です。ファイル情報を追加します: {file}')

                                                if file_ext in IMAGE_EXTENSIONS:
                                                    file_size = os.path.getsize(file_path)
                                                    markdown_content = f"# {file}\n\n"
                                                    markdown_content += f"画像ファイル: `{file}`\n\n"
                                                    markdown_content += f"- ファイルパス: `{file_path}`\n"
                                                    markdown_content += f"- ファイルサイズ: {file_size:,} バイト\n"
                                                    markdown_content += f"- 拡張子: {file_ext}\n\n"

                                                    if ollama_url and ollama_model:
                                                        log_message(f'Ollamaで画像説明を生成します: {file}')
                                                        loop = asyncio.get_running_loop()
                                                        description = await loop.run_in_executor(None, generate_image_description_with_ollama_sync, file_path, ollama_url, ollama_model)
                                                        if description:
                                                            markdown_content += f"## 画像の説明 (Ollama {ollama_model})\n\n"
                                                            markdown_content += f"{description}\n\n"
                                                        else:
                                                            markdown_content += "注: Ollamaでの画像説明生成に失敗しました。\n\n"

                                                    markdown_content += f"![{file}]({file_path})\n\n"

                                                    if not ollama_url or not ollama_model:
                                                        markdown_content += "注: この画像にはテキスト情報が含まれていないか、OCR処理でテキストが検出されませんでした。\n"
                                                        markdown_content += "画像の内容を説明するには、Ollama (llava モデル推奨) を使用してください。\n"
                                                else:
                                                    markdown_content = f"# {file}\n\n変換結果が空でした。\n"

                                            timestamp = datetime.now().strftime('%Y%m%d%H%M%S')
                                            name_without_ext = os.path.splitext(file)[0]
                                            output_filename = f'{name_without_ext}_{timestamp}.md'
                                            output_path = os.path.join(root, output_filename)

                                            with open(output_path, 'w', encoding='utf-8') as f:
                                                f.write(markdown_content)

                                            converted_count += 1
                                            log_message(f'フォルダ内ファイルを変換: {file} → {output_filename} (コンテンツ長: {len(markdown_content)}文字)')
                                        except Exception as e:
                                            log_message(f'フォルダ内ファイル変換エラー: {file} - {str(e)}')
                                    else:
                                        log_message(f'サポートされていないファイル形式: {file}')

                                except Exception as e:
                                    log_message(f'フォルダ内ファイル処理エラー: {file} - {str(e)}')

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