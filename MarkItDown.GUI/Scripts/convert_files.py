import os
import sys
import json
import traceback
from datetime import datetime
import base64

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

    def generate_image_description_with_ollama(image_path, ollama_url, ollama_model):
        """Ollamaを使用して画像の説明を生成する"""
        try:
            log_message(f'Ollamaで画像説明を生成中: {image_path}')
            
            # requestsライブラリのインポートを確認
            try:
                import requests
                log_message('requestsライブラリのインポートに成功')
            except ImportError as e:
                log_message(f'requestsライブラリのインポートに失敗: {e}')
                return None
            
            # 画像ファイルの存在確認
            if not os.path.exists(image_path):
                log_message(f'画像ファイルが見つかりません: {image_path}')
                return None
            
            # 画像をBase64エンコード
            try:
                with open(image_path, 'rb') as img_file:
                    image_bytes = img_file.read()
                    image_size_mb = len(image_bytes) / (1024 * 1024)
                    image_data = base64.b64encode(image_bytes).decode('utf-8')
                
                log_message(f'画像をBase64エンコードしました（元サイズ: {image_size_mb:.2f} MB, Base64: {len(image_data)}文字）')
                
                # 大きな画像の警告
                if image_size_mb > 2:
                    log_message(f'警告: 画像サイズが大きいため、処理に時間がかかる可能性があります（{image_size_mb:.2f} MB）')
                
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
            
            # Ollamaサーバーの接続確認
            try:
                test_response = requests.get(f"{ollama_url}/api/tags", timeout=5)
                if test_response.status_code != 200:
                    log_message(f'Ollamaサーバーへの接続に失敗しました: {test_response.status_code}')
                    return None
            except Exception as e:
                log_message(f'Ollamaサーバーへの接続エラー: {e}')
                return None
            
            log_message(f'Ollama APIリクエスト送信: {api_url}')
            log_message(f'モデル: {ollama_model}')
            log_message('画像説明を生成中... (最大5分かかる場合があります)')
            
            try:
                response = requests.post(api_url, json=payload, timeout=300)
                log_message(f'レスポンス受信: ステータスコード {response.status_code}')
            except requests.exceptions.Timeout:
                log_message('Ollama APIタイムアウト（300秒/5分）')
                log_message('画像が大きすぎるか、モデルの処理に時間がかかっています')
                return None
            except requests.exceptions.ConnectionError as e:
                log_message(f'Ollama API接続エラー: {e}')
                return None
            except Exception as e:
                log_message(f'Ollama APIリクエストエラー: {e}')
                return None
            
            if response.status_code == 200:
                try:
                    result = response.json()
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
                
        except Exception as e:
            log_message(f'Ollama画像説明生成エラー: {e}')
            import traceback
            log_message(f'スタックトレース: {traceback.format_exc()}')
            return None

    def convert_files(file_paths, folder_paths):
        results = []

        log_message(f'Start convert_files: {len(file_paths)} files, {len(folder_paths)} folders')

        try:
            # Import MarkItDown library once at the beginning
            log_message('Importing MarkItDown library...')
            import markitdown
            log_message('MarkItDown library imported successfully')

            # Create a single MarkItDown instance to reuse
            # 画像処理のために必要なオプションを確認
            log_message('MarkItDownインスタンスを作成中...')
            try:
                md = markitdown.MarkItDown()
                log_message('MarkItDown instance created')
            except Exception as e:
                log_message(f'MarkItDownインスタンス作成エラー: {e}')
                raise

            # Process files
            for file_path in file_paths:
                log_message(f'ファイル処理開始: {file_path}')
                if os.path.exists(file_path):
                    try:
                        # ファイルの拡張子を取得
                        file_name = os.path.basename(file_path)
                        file_dir = os.path.dirname(file_path)
                        name_without_ext = os.path.splitext(file_name)[0]
                        
                        log_message(f'ファイル名: {file_name}')
                        log_message(f'ディレクトリ: {file_dir}')
                        log_message(f'拡張子なし名: {name_without_ext}')
                        
                        # Convert file using MarkItDown (reuse instance)
                        log_message('Converting file with MarkItDown...')
                        result = md.convert(file_path)
                        log_message(f'変換結果オブジェクト: {type(result)}')
                        
                        markdown_content = result.text_content
                        log_message(f'変換完了、コンテンツ長: {len(markdown_content)}文字')
                        
                        # 画像ファイルの場合、変換結果が空になることがあるため、ファイル情報を追加
                        if not markdown_content or len(markdown_content.strip()) == 0:
                            log_message(f'警告: 変換結果が空です。ファイル情報を追加します。')
                            
                            # ファイル拡張子を確認
                            file_ext = os.path.splitext(file_path)[1].lower()
                            image_extensions = ['.jpg', '.jpeg', '.png', '.gif', '.bmp', '.tiff', '.tif', '.webp']
                            
                            if file_ext in image_extensions:
                                # 画像ファイルの場合、画像へのリンクとファイル情報を生成
                                file_size = os.path.getsize(file_path)
                                markdown_content = f"# {file_name}\n\n"
                                markdown_content += f"画像ファイル: `{file_name}`\n\n"
                                markdown_content += f"- ファイルパス: `{file_path}`\n"
                                markdown_content += f"- ファイルサイズ: {file_size:,} バイト\n"
                                markdown_content += f"- 拡張子: {file_ext}\n\n"
                                
                                # Ollamaで画像説明を試みる
                                if ollama_url and ollama_model:
                                    log_message('Ollamaで画像説明を生成します...')
                                    description = generate_image_description_with_ollama(file_path, ollama_url, ollama_model)
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
                                # その他のファイルの場合、基本的なファイル情報を生成
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
                        results.append(f'変換完了: {file_name} → {output_filename}')
                        log_message(f'ファイルを変換しました: {file_path} → {output_path}')
                        
                    except Exception as e:
                        error_type = type(e).__name__
                        error_msg = str(e)
                        
                        # サポートされていないファイル形式の場合
                        if 'UnsupportedFormatException' in error_type or 'not supported' in error_msg.lower():
                            log_message(f'サポートされていないファイル形式: {file_path}')
                            log_message(f'このファイル形式は MarkItDown でサポートされていません: {file_ext}')
                            
                            # サポート外ファイルの基本情報を出力
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
                            
                            # タイムスタンプを生成
                            timestamp = datetime.now().strftime('%Y%m%d%H%M%S')
                            output_filename = f'{name_without_ext}_{timestamp}.md'
                            output_path = os.path.join(file_dir, output_filename)
                            
                            with open(output_path, 'w', encoding='utf-8') as f:
                                f.write(markdown_content)
                            
                            results.append(f'サポート外: {file_name}')
                            log_message(f'サポート外ファイル情報を出力しました: {output_path}')
                        else:
                            # その他のエラー
                            error_msg = f'変換エラー: {file_path} - {str(e)}'
                            results.append(error_msg)
                            log_message(error_msg)
                            import traceback
                            traceback.print_exc()
                else:
                    log_message(f'ファイルが存在しません: {file_path}')
        
        except ImportError as e:
            log_message(f'MarkItDownライブラリのインポートに失敗: {e}')
            log_message('MarkItDownライブラリが取得できませんでした、アプリを終了します。')
            results.append('MarkItDownライブラリが取得できませんでした、アプリを終了します。')
            return results
        
        # フォルダの処理
        for folder_path in folder_paths:
            log_message(f'フォルダ処理開始: {folder_path}')
            if os.path.exists(folder_path):
                try:
                    folder_name = os.path.basename(folder_path)
                    # フォルダ内のファイルを再帰的に処理
                    converted_count = 0
                    for root, dirs, files in os.walk(folder_path):
                        for file in files:
                            file_path = os.path.join(root, file)
                            try:
                                # サポートされているファイル形式かチェック
                                supported_extensions = [
                                    # テキストファイル
                                    '.txt', '.md', '.html', '.htm', '.csv', '.json', '.xml',
                                    # Officeドキュメント
                                    '.docx', '.doc', '.xlsx', '.xls', '.pptx', '.ppt',
                                    # リッチメディアファイル
                                    '.jpg', '.jpeg', '.png', '.gif', '.bmp', '.tiff', '.tif',
                                    '.mp3', '.wav', '.flac', '.aac', '.ogg',
                                    # アーカイブ
                                    '.zip', '.rar', '.7z', '.tar', '.gz'
                                ]
                                file_ext = os.path.splitext(file)[1].lower()
                                
                                log_message(f'フォルダ内ファイル: {file} (拡張子: {file_ext})')
                                
                                if file_ext in supported_extensions:
                                    log_message(f'Supported file format: {file}')
                                    # Convert file using MarkItDown (reuse instance)
                                    try:
                                        result = md.convert(file_path)
                                        markdown_content = result.text_content
                                        
                                        # 画像ファイルの場合、変換結果が空になることがあるため、ファイル情報を追加
                                        if not markdown_content or len(markdown_content.strip()) == 0:
                                            log_message(f'警告: フォルダ内ファイルの変換結果が空です。ファイル情報を追加します: {file}')
                                            
                                            # ファイル拡張子を確認
                                            image_extensions = ['.jpg', '.jpeg', '.png', '.gif', '.bmp', '.tiff', '.tif', '.webp']
                                            
                                            if file_ext in image_extensions:
                                                # 画像ファイルの場合、画像へのリンクとファイル情報を生成
                                                file_size = os.path.getsize(file_path)
                                                markdown_content = f"# {file}\n\n"
                                                markdown_content += f"画像ファイル: `{file}`\n\n"
                                                markdown_content += f"- ファイルパス: `{file_path}`\n"
                                                markdown_content += f"- ファイルサイズ: {file_size:,} バイト\n"
                                                markdown_content += f"- 拡張子: {file_ext}\n\n"
                                                
                                                # Ollamaで画像説明を試みる
                                                if ollama_url and ollama_model:
                                                    log_message(f'Ollamaで画像説明を生成します: {file}')
                                                    description = generate_image_description_with_ollama(file_path, ollama_url, ollama_model)
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
                                                # その他のファイルの場合、基本的なファイル情報を生成
                                                markdown_content = f"# {file}\n\n変換結果が空でした。\n"
                                        
                                        # タイムスタンプを生成
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
    
    # ファイルとフォルダを変換
    results = convert_files(file_paths, folder_paths)
    
    # 結果を出力
    for result in results:
        log_message(result)
    
    log_message('Pythonスクリプト完了')
    
except Exception as e:
    log_message('Pythonスクリプト実行中にエラー: ' + str(e))
    log_message('スタックトレース: ' + traceback.format_exc())
    sys.exit(1) 