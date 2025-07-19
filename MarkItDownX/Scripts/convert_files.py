import os
import sys
import json
import traceback

def log_message(message):
    print(message, flush=True)

try:
    log_message('Pythonスクリプト開始')
    
    # アプリケーションディレクトリを取得
    app_dir = os.path.dirname(os.path.abspath('__file__'))
    log_message('アプリケーションディレクトリ: ' + app_dir)

    def convert_files(file_paths, folder_paths):
        results = []
        
        log_message(f'convert_files開始: ファイル {len(file_paths)}個, フォルダ {len(folder_paths)}個')
        
        try:
            # MarkItDownライブラリをインポート
            log_message('MarkItDownライブラリをインポート中...')
            import markitdown
            log_message('MarkItDownライブラリを正常にインポートしました')
            
            # ファイルの変換処理
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
                        
                        # MarkItDownでファイルを変換（直接ファイルを渡す）
                        log_message('MarkItDownでファイルを変換中...')
                        md = markitdown.MarkItDown()
                        result = md.convert(file_path)
                        markdown_content = result.text_content
                        log_message(f'変換完了、コンテンツ長: {len(markdown_content)}文字')
                        
                        # 同じディレクトリにMarkdownファイルとして出力
                        output_path = os.path.join(file_dir, name_without_ext + '.md')
                        log_message(f'出力パス: {output_path}')
                        
                        with open(output_path, 'w', encoding='utf-8') as f:
                            f.write(markdown_content)
                        
                        log_message(f'ファイル出力完了: {output_path}')
                        results.append(f'変換完了: {file_name} → {name_without_ext}.md')
                        log_message(f'ファイルを変換しました: {file_path} → {output_path}')
                        
                    except Exception as e:
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
                                    log_message(f'サポートされているファイル形式: {file}')
                                    # MarkItDownでファイルを変換
                                    try:
                                        md = markitdown.MarkItDown()
                                        result = md.convert(file_path)
                                        markdown_content = result.text_content
                                        
                                        # 同じディレクトリにMarkdownファイルとして出力
                                        name_without_ext = os.path.splitext(file)[0]
                                        output_path = os.path.join(root, name_without_ext + '.md')
                                        
                                        with open(output_path, 'w', encoding='utf-8') as f:
                                            f.write(markdown_content)
                                        
                                        converted_count += 1
                                        log_message(f'フォルダ内ファイルを変換: {file} → {name_without_ext}.md')
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