#!/usr/bin/env python3
"""
Instagram 専用スクレイピングスクリプト。
指定ユーザーの全投稿の画像・動画をダウンロードする。

認証フロー（優先順位順）:
  1. 保存済みセッションファイルがあれば Instaloader に読み込んで検証
  2. ブラウザ (Chrome/Firefox/Edge) のCookieから自動取得（browser_cookie3）
  3. stdin経由でユーザー名/パスワードを受け取りInstaloaderでログイン

Usage:
    python scrape_instagram.py <target_username> <output_dir>

環境変数:
    IG_SESSION_DIR: セッションファイル保存ディレクトリ

終了コード:
    0: 正常完了
    1: 致命的エラー
    2: instaloader 未インストール
    3: セッション切れ（再ログインが必要）
    4: ログイン情報が必要（C#側からのstdin入力待ち失敗）
"""

import json
import os
import random
import sys
import time
import traceback
from pathlib import Path


def log(msg: str):
    """タイムスタンプ付きログ出力（C#側でアイドルタイムアウトをリセットする）"""
    print(f"[Instagram] {msg}", flush=True)


def check_dependencies() -> bool:
    """必要なパッケージがインポートできるかチェック"""
    try:
        import instaloader  # noqa: F401
        return True
    except ImportError:
        log("instaloader パッケージがインストールされていないのだ")
        return False


# ────────────────────────────────────────────
#  セッション管理
# ────────────────────────────────────────────

def _get_session_file(session_dir: str, ig_username: str) -> str:
    return os.path.join(session_dir, f"session-{ig_username}")


def _get_session_info_file(session_dir: str) -> str:
    return os.path.join(session_dir, "session_info.json")


def _save_session_info(session_dir: str, ig_username: str):
    info_file = _get_session_info_file(session_dir)
    with open(info_file, "w", encoding="utf-8") as f:
        json.dump({"username": ig_username}, f)


def _load_session_info(session_dir: str) -> str | None:
    info_file = _get_session_info_file(session_dir)
    if not os.path.exists(info_file):
        return None
    try:
        with open(info_file, "r", encoding="utf-8") as f:
            data = json.load(f)
            return data.get("username")
    except (json.JSONDecodeError, OSError):
        return None


def _setup_instaloader():
    """Instaloader インスタンスを構成する（未ログイン状態）"""
    import instaloader

    L = instaloader.Instaloader(
        download_pictures=True,
        download_videos=True,
        download_video_thumbnails=False,
        download_geotags=False,
        download_comments=False,
        save_metadata=False,
        compress_json=False,
        post_metadata_txt_pattern="",
        storyitem_metadata_txt_pattern="",
        filename_pattern="{shortcode}_{date_utc:%Y%m%d_%H%M%S}",
    )
    return L


def _try_load_session(L, session_dir: str) -> str | None:
    """保存済みセッションの読み込みを試みる。成功すればユーザー名を返す。"""
    ig_username = _load_session_info(session_dir)
    if not ig_username:
        log("保存済みのログイン情報が見つからないのだ。")
        return None

    session_file = _get_session_file(session_dir, ig_username)
    if not os.path.exists(session_file):
        log(f"セッションファイルが見つからないのだ: {session_file}")
        return None

    try:
        L.load_session_from_file(ig_username, session_file)
        test_user = L.test_login()
        if test_user:
            log(f"セッション有効: ログインユーザー = {test_user}")
            return test_user
        else:
            log("セッションが無効なのだ（test_login が None を返した）。")
            return None
    except Exception as e:
        log(f"セッション読み込みエラー: {e}")
        return None


# ────────────────────────────────────────────
#  ブラウザCookie自動取得（直接SQLite読み取り）
# ────────────────────────────────────────────

def _get_chrome_cookies_paths() -> list[tuple[str, str]]:
    """Chrome系ブラウザのCookieファイルパスとLocal Stateパスのリストを返す。"""
    results = []
    local_app = os.environ.get("LOCALAPPDATA", "")
    if not local_app:
        return results

    browsers = [
        ("Chrome", os.path.join(local_app, "Google", "Chrome", "User Data")),
        ("Edge", os.path.join(local_app, "Microsoft", "Edge", "User Data")),
        ("Brave", os.path.join(local_app, "BraveSoftware", "Brave-Browser", "User Data")),
    ]

    for name, user_data_dir in browsers:
        if not os.path.isdir(user_data_dir):
            continue
        local_state = os.path.join(user_data_dir, "Local State")
        if not os.path.isfile(local_state):
            continue
        # Default プロファイルと Profile 1〜5 を探索
        for profile in ["Default", "Profile 1", "Profile 2", "Profile 3", "Profile 4", "Profile 5"]:
            cookie_file = os.path.join(user_data_dir, profile, "Network", "Cookies")
            if not os.path.isfile(cookie_file):
                cookie_file = os.path.join(user_data_dir, profile, "Cookies")
            if os.path.isfile(cookie_file):
                results.append((f"{name} ({profile})", cookie_file, local_state))
    return results


def _decrypt_chrome_cookie_value(encrypted_value: bytes, key: bytes) -> str | None:
    """Chrome v10+ のAES-GCM暗号化Cookieを復号する。"""
    try:
        if encrypted_value[:3] == b"v10" or encrypted_value[:3] == b"v20":
            # v10/v20: AES-256-GCM
            nonce = encrypted_value[3:15]
            ciphertext = encrypted_value[15:]
            from Cryptodome.Cipher import AES
            cipher = AES.new(key, AES.MODE_GCM, nonce=nonce)
            # 最後の16バイトがタグ
            decrypted = cipher.decrypt_and_verify(ciphertext[:-16], ciphertext[-16:])
            return decrypted.decode("utf-8", errors="replace")
        else:
            # 非暗号化 or DPAPI (旧Windows)
            try:
                import ctypes
                import ctypes.wintypes
                class DATA_BLOB(ctypes.Structure):
                    _fields_ = [("cbData", ctypes.wintypes.DWORD),
                                ("pbData", ctypes.POINTER(ctypes.c_char))]
                blob_in = DATA_BLOB(len(encrypted_value), ctypes.create_string_buffer(encrypted_value, len(encrypted_value)))
                blob_out = DATA_BLOB()
                if ctypes.windll.crypt32.CryptUnprotectData(ctypes.byref(blob_in), None, None, None, None, 0, ctypes.byref(blob_out)):
                    raw = ctypes.string_at(blob_out.pbData, blob_out.cbData)
                    ctypes.windll.kernel32.LocalFree(blob_out.pbData)
                    return raw.decode("utf-8", errors="replace")
            except Exception:
                pass
            return None
    except Exception:
        return None


def _get_chrome_encryption_key(local_state_path: str) -> bytes | None:
    """Chrome Local State ファイルから暗号化キーを取得する。"""
    try:
        with open(local_state_path, "r", encoding="utf-8") as f:
            data = json.load(f)
        encrypted_key_b64 = data["os_crypt"]["encrypted_key"]
        import base64
        encrypted_key = base64.b64decode(encrypted_key_b64)
        # "DPAPI" プレフィックスを除去
        encrypted_key = encrypted_key[5:]
        # DPAPI で復号
        import ctypes
        import ctypes.wintypes
        class DATA_BLOB(ctypes.Structure):
            _fields_ = [("cbData", ctypes.wintypes.DWORD),
                        ("pbData", ctypes.POINTER(ctypes.c_char))]
        blob_in = DATA_BLOB(len(encrypted_key), ctypes.create_string_buffer(encrypted_key, len(encrypted_key)))
        blob_out = DATA_BLOB()
        if ctypes.windll.crypt32.CryptUnprotectData(ctypes.byref(blob_in), None, None, None, None, 0, ctypes.byref(blob_out)):
            key = ctypes.string_at(blob_out.pbData, blob_out.cbData)
            ctypes.windll.kernel32.LocalFree(blob_out.pbData)
            return key
        return None
    except Exception as e:
        log(f"暗号化キー取得エラー: {e}")
        return None


def _copy_locked_file(src: str, dst: str) -> bool:
    """
    ロックされたファイルをコピーする。複数の方法を試行。
    1. shutil.copy2（通常コピー）
    2. バイナリ読み込み（共有モード）
    3. Win32 CopyFile API
    4. sqlite3 backup API（SQLiteファイル用）
    5. Win32 CreateFile (FILE_SHARE_READ|WRITE|DELETE) + ReadFile
    """
    import shutil

    # 方法1: 通常コピー
    try:
        shutil.copy2(src, dst)
        log(f"  コピー成功（shutil.copy2）")
        return True
    except (OSError, PermissionError) as e:
        log(f"  shutil.copy2 失敗: {e}")

    # 方法2: バイナリ読み込み（Python open）
    try:
        with open(src, "rb") as f_in:
            data = f_in.read()
        with open(dst, "wb") as f_out:
            f_out.write(data)
        log(f"  コピー成功（open rb）")
        return True
    except (OSError, PermissionError) as e:
        log(f"  open(rb) 失敗: {e}")

    # 方法3: Win32 CopyFileW API
    try:
        import ctypes
        result = ctypes.windll.kernel32.CopyFileW(src, dst, False)
        if result:
            log(f"  コピー成功（Win32 CopyFileW）")
            return True
        else:
            err = ctypes.get_last_error()
            log(f"  Win32 CopyFileW 失敗: error={err}")
    except Exception as e:
        log(f"  Win32 CopyFileW 例外: {e}")

    # 方法4: sqlite3 backup API
    try:
        import sqlite3
        src_conn = sqlite3.connect(f"file:{src}?mode=ro&nolock=1", uri=True)
        dst_conn = sqlite3.connect(dst)
        src_conn.backup(dst_conn)
        dst_conn.close()
        src_conn.close()
        log(f"  コピー成功（sqlite3 backup）")
        return True
    except Exception as e:
        log(f"  sqlite3 backup 失敗: {e}")

    # 方法5: Win32 CreateFile with full share mode
    try:
        import ctypes
        import ctypes.wintypes

        GENERIC_READ = 0x80000000
        FILE_SHARE_READ = 0x00000001
        FILE_SHARE_WRITE = 0x00000002
        FILE_SHARE_DELETE = 0x00000004
        OPEN_EXISTING = 3
        FILE_ATTRIBUTE_NORMAL = 0x80
        INVALID_HANDLE_VALUE = ctypes.wintypes.HANDLE(-1).value

        handle = ctypes.windll.kernel32.CreateFileW(
            src,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            None,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            None,
        )
        if handle == INVALID_HANDLE_VALUE:
            err = ctypes.get_last_error()
            log(f"  Win32 CreateFileW 失敗: error={err}")
        else:
            # ファイルサイズ取得
            file_size = ctypes.wintypes.DWORD(0)
            ctypes.windll.kernel32.GetFileSize(handle, ctypes.byref(file_size))
            size = file_size.value
            if size == 0xFFFFFFFF:
                size = 0  # サイズ取得失敗の場合

            # チャンクで読み込み
            buf = ctypes.create_string_buffer(1024 * 1024)  # 1MB
            bytes_read = ctypes.wintypes.DWORD(0)
            chunks = []
            while True:
                success = ctypes.windll.kernel32.ReadFile(
                    handle, buf, len(buf), ctypes.byref(bytes_read), None
                )
                if not success or bytes_read.value == 0:
                    break
                chunks.append(buf.raw[:bytes_read.value])

            ctypes.windll.kernel32.CloseHandle(handle)

            data = b"".join(chunks)
            if data:
                with open(dst, "wb") as f_out:
                    f_out.write(data)
                log(f"  コピー成功（Win32 CreateFileW, {len(data)} bytes）")
                return True
            else:
                log(f"  Win32 CreateFileW: 読み取りデータが空")
    except Exception as e:
        log(f"  Win32 CreateFileW 例外: {e}")

    return False


def _read_chrome_cookies(cookie_file: str, local_state_path: str, domain: str) -> dict[str, str]:
    """
    Chrome系ブラウザのCookieファイルをコピーしてSQLiteで読み取り、
    指定ドメインのCookieを復号して返す。
    ブラウザ起動中でもファイルコピーで対応。
    """
    import sqlite3
    import tempfile

    # 暗号化キーを取得
    key = _get_chrome_encryption_key(local_state_path)
    if not key:
        log("暗号化キーの取得に失敗したのだ。")
        return {}

    # Cookieファイルを一時ファイルにコピー（ブラウザ起動中のロック回避）
    cookies = {}
    tmp_path = None
    tmp_wal = None
    try:
        with tempfile.NamedTemporaryFile(delete=False, suffix=".db") as tmp:
            tmp_path = tmp.name

        if not _copy_locked_file(cookie_file, tmp_path):
            log(f"Cookieファイルのコピーに全て失敗したのだ: {cookie_file}")
            return {}

        # WAL ファイルもコピー（存在すれば）
        wal_file = cookie_file + "-wal"
        if os.path.exists(wal_file):
            tmp_wal = tmp_path + "-wal"
            _copy_locked_file(wal_file, tmp_wal)

        conn = sqlite3.connect(tmp_path)
        conn.text_factory = bytes
        cursor = conn.cursor()
        cursor.execute(
            "SELECT name, encrypted_value, host_key FROM cookies WHERE host_key LIKE ?",
            (f"%{domain}%",)
        )
        for name_bytes, encrypted_value, host_key_bytes in cursor.fetchall():
            name = name_bytes.decode("utf-8", errors="replace") if isinstance(name_bytes, bytes) else name_bytes
            value = _decrypt_chrome_cookie_value(encrypted_value, key)
            if value:
                cookies[name] = value
        conn.close()
    except Exception as e:
        log(f"Cookie読み取りエラー: {e}")
    finally:
        for p in [tmp_path, tmp_wal]:
            if p and os.path.exists(p):
                try:
                    os.remove(p)
                except OSError:
                    pass

    return cookies


def _try_import_browser_cookies(L, session_dir: str) -> str | None:
    """
    Chrome系ブラウザのCookieファイルから直接Instagram Cookieを読み取り、
    Instaloaderのセッションにセットする。
    ブラウザが起動中でもファイルコピー方式で対応。
    """
    browser_paths = _get_chrome_cookies_paths()

    if not browser_paths:
        log("Chrome系ブラウザのCookieファイルが見つからないのだ。")
        return None

    for browser_name, cookie_file, local_state in browser_paths:
        try:
            log(f"{browser_name} のCookieを確認中...")
            cookies = _read_chrome_cookies(cookie_file, local_state, "instagram.com")

            if not cookies:
                log(f"{browser_name} にInstagramのCookieが見つからないのだ。")
                continue

            session_id = cookies.get("sessionid")
            if not session_id:
                log(f"{browser_name} にInstagramのsessionidが見つからないのだ（ログインしていない可能性）。")
                continue

            log(f"{browser_name} からInstagramのセッションCookieを取得したのだ！（Cookie数: {len(cookies)}）")

            # Instaloaderのセッションにcookieをセット
            L.context._session.cookies.update(cookies)

            # セッション検証
            test_user = L.test_login()
            if test_user:
                log(f"ブラウザCookieでログイン成功: ユーザー = {test_user}")
                # セッションを保存（次回以降はブラウザ不要）
                session_file = _get_session_file(session_dir, test_user)
                L.context.username = test_user
                L.save_session_to_file(session_file)
                _save_session_info(session_dir, test_user)
                log("セッションを保存したのだ。次回以降はブラウザCookie不要。")
                return test_user
            else:
                log(f"{browser_name} のCookieでログインできなかったのだ（セッション期限切れの可能性）。")

        except Exception as e:
            log(f"{browser_name} のCookie取得中にエラー: {e}")
            traceback.print_exc()

    return None


# ────────────────────────────────────────────
#  stdin経由のインタラクティブログイン（フォールバック）
# ────────────────────────────────────────────

def _login_interactive(L, session_dir: str) -> str:
    """
    stdin からユーザー名/パスワードを受け取り、Instaloader でログインする。
    """
    import instaloader

    log("[INPUT_REQUIRED] LOGIN")
    log("Instagramのログイン情報を入力してください。")

    try:
        ig_username = input().strip()
        ig_password = input().strip()
    except EOFError:
        log("ログイン情報の入力を受け取れなかったのだ。")
        sys.exit(4)

    if not ig_username or not ig_password:
        log("ユーザー名またはパスワードが空なのだ。")
        sys.exit(4)

    log(f"ログイン中... (ユーザー: {ig_username})")

    try:
        L.login(ig_username, ig_password)
    except instaloader.exceptions.TwoFactorAuthRequiredException:
        # 2要素認証
        log("[INPUT_REQUIRED] 2FA")
        log("2段階認証コードを入力してください。")
        try:
            code = input().strip()
        except EOFError:
            log("2FAコードの入力を受け取れなかったのだ。")
            sys.exit(4)

        try:
            L.two_factor_login(code)
        except Exception as e:
            log(f"2段階認証に失敗したのだ: {e}")
            log("※ InstaloaderのAPIではSMSベースの2FAのみ対応しています。")
            log("※ 認証アプリ（Google Authenticator等）の2FAは非対応です。")
            log("※ 代替策: 普段使いのブラウザでInstagramにログインした状態で再実行してください。")
            log("  ブラウザのCookieから自動的にセッションを取得します。")
            sys.exit(1)

    except instaloader.exceptions.BadCredentialsException:
        log("ユーザー名またはパスワードが正しくないのだ。")
        sys.exit(1)

    except instaloader.exceptions.ConnectionException as e:
        error_str = str(e).lower()
        if "checkpoint" in error_str:
            log(f"Instagramがセキュリティチェックを要求しているのだ。")
            log("ブラウザでInstagramにログインしてチェックを完了させてから再実行してください。")
        elif "400" in str(e):
            log(f"ログインリクエストが拒否されたのだ。しばらく時間を置いてから再試行してください: {e}")
        else:
            log(f"ログイン中に接続エラーが発生したのだ: {e}")
        sys.exit(1)

    # ログイン成功 → セッション保存
    session_file = _get_session_file(session_dir, ig_username)
    L.save_session_to_file(session_file)
    _save_session_info(session_dir, ig_username)
    log(f"ログイン成功！セッションを保存したのだ。")
    return ig_username


# ────────────────────────────────────────────
#  メディアダウンロード
# ────────────────────────────────────────────

def download_media(L, target_username: str, output_dir: str):
    """Instaloader を使ってユーザーの全投稿メディアをダウンロードする。"""
    import instaloader

    log(f"=== Phase 1: @{target_username} のプロフィール取得 ===")

    try:
        profile = instaloader.Profile.from_username(L.context, target_username)
    except instaloader.exceptions.ProfileNotExistsException:
        log(f"ユーザー @{target_username} が見つからないのだ。")
        sys.exit(1)
    except instaloader.exceptions.ConnectionException as e:
        error_str = str(e).lower()
        if "login" in error_str or "401" in str(e) or "checkpoint" in error_str:
            log(f"セッションが無効またはチェックポイントが必要なのだ: {e}")
            sys.exit(3)
        raise
    except instaloader.exceptions.LoginRequiredException:
        log("ログインが必要なのだ。セッションが切れている可能性があるのだ。")
        sys.exit(3)

    post_count = profile.mediacount
    log(f"プロフィール取得完了: @{target_username} (投稿数: {post_count})")

    log(f"=== Phase 2: メディアダウンロード ===")
    log(f"出力先: {output_dir}")

    downloaded = 0
    skipped = 0
    failed = 0
    total_processed = 0

    try:
        posts = profile.get_posts()
        for post in posts:
            total_processed += 1

            try:
                type_str = "画像"
                if post.is_video:
                    type_str = "動画"
                elif post.typename == "GraphSidecar":
                    type_str = "カルーセル"

                log(f"投稿 {total_processed}: {post.shortcode} ({type_str}, {post.date_utc.strftime('%Y-%m-%d')})")

                success = L.download_post(post, target=Path(output_dir))

                if success:
                    downloaded += 1
                else:
                    skipped += 1

                if total_processed % 10 == 0:
                    log(f"--- 進捗: {total_processed}/{post_count} 投稿処理済み (DL: {downloaded}, スキップ: {skipped}, 失敗: {failed}) ---")

                time.sleep(random.uniform(0.5, 1.5))

            except instaloader.exceptions.ConnectionException as e:
                error_str = str(e).lower()
                if "login" in error_str or "401" in str(e) or "checkpoint" in error_str:
                    log(f"セッションが無効になったのだ（{total_processed}投稿目で検出）: {e}")
                    sys.exit(3)
                if "429" in str(e) or "too many" in error_str:
                    log(f"レートリミット検出。60秒待機するのだ...")
                    time.sleep(60)
                    failed += 1
                else:
                    log(f"投稿 {post.shortcode} のDL中に接続エラー: {e}")
                    failed += 1
                    time.sleep(5)

            except instaloader.exceptions.LoginRequiredException:
                log(f"ログインが必要になったのだ（{total_processed}投稿目で検出）。")
                sys.exit(3)

            except Exception as e:
                log(f"投稿 {post.shortcode} のDL中にエラー: {e}")
                failed += 1
                time.sleep(2)

    except instaloader.exceptions.QueryReturnedNotFoundException:
        log(f"ユーザー @{target_username} の投稿が取得できなかったのだ（非公開アカウントの可能性）。")
    except instaloader.exceptions.LoginRequiredException:
        log("ログインが必要なのだ。セッションが切れている可能性があるのだ。")
        sys.exit(3)
    except instaloader.exceptions.ConnectionException as e:
        if "login" in str(e).lower() or "checkpoint" in str(e).lower():
            log(f"セッションが無効なのだ: {e}")
            sys.exit(3)
        log(f"接続エラーで中断されたのだ: {e}")

    _cleanup_metadata_files(output_dir)

    log(f"=== 完了! 投稿: {total_processed}/{post_count}, DL: {downloaded}, スキップ: {skipped}, 失敗: {failed} ===")


def _cleanup_metadata_files(output_dir: str):
    """Instaloader が生成するメタデータファイル（txt, json.xz 等）を削除する"""
    removed = 0
    for root, dirs, files in os.walk(output_dir):
        for f in files:
            lower = f.lower()
            if not lower.endswith((".jpg", ".jpeg", ".png", ".mp4", ".webp")):
                filepath = os.path.join(root, f)
                try:
                    os.remove(filepath)
                    removed += 1
                except OSError:
                    pass
    if removed > 0:
        log(f"メタデータファイル {removed} 件を削除したのだ")


# ────────────────────────────────────────────
#  メイン処理
# ────────────────────────────────────────────

def _extract_shortcode_from_url(url: str) -> str | None:
    """Instagram URLから shortcode を抽出する。"""
    import re
    match = re.search(r"instagram\.com/(?:p|reel|reels)/([A-Za-z0-9_-]+)", url)
    return match.group(1) if match else None


def download_single_post(L, shortcode: str, output_dir: str):
    """単一の投稿/リールをダウンロードする。"""
    import instaloader

    log(f"=== 投稿のダウンロード: {shortcode} ===")

    try:
        post = instaloader.Post.from_shortcode(L.context, shortcode)
    except instaloader.exceptions.LoginRequiredException:
        log("ログインが必要なのだ。セッションが切れている可能性があるのだ。")
        sys.exit(3)
    except Exception as e:
        log(f"投稿の取得に失敗したのだ: {e}")
        sys.exit(1)

    type_str = "画像"
    if post.is_video:
        type_str = "動画"
    elif post.typename == "GraphSidecar":
        type_str = "カルーセル"

    log(f"投稿情報: {shortcode} ({type_str}, {post.date_utc.strftime('%Y-%m-%d')}, owner: @{post.owner_username})")

    try:
        success = L.download_post(post, target=Path(output_dir))
        if success:
            log("投稿のダウンロードに成功したのだ！")
        else:
            log("投稿のダウンロードをスキップしたのだ（既にダウンロード済み）。")
    except Exception as e:
        log(f"投稿のダウンロード中にエラー: {e}")
        sys.exit(1)

    _cleanup_metadata_files(output_dir)
    log(f"=== 完了! 投稿: 1/1, DL: 1, スキップ: 0, 失敗: 0 ===")


def main():
    if len(sys.argv) < 3:
        print("Usage: python scrape_instagram.py <target_username_or_url> <output_dir>", file=sys.stderr)
        sys.exit(1)

    target = sys.argv[1]
    output_dir = sys.argv[2]

    # URLか、ユーザー名かを判定
    shortcode = None
    if target.startswith("http://") or target.startswith("https://"):
        shortcode = _extract_shortcode_from_url(target)
        if shortcode:
            target_username = None
            log(f"=== Instagram 投稿スクレイピング開始: {shortcode} ===")
        else:
            # URLだがshortcodeが見つからない場合はユーザー名として扱う
            target_username = target.rstrip("/").split("/")[-1].lstrip("@")
            log(f"=== Instagram 専用スクレイピング開始: @{target_username} ===")
    else:
        target_username = target.lstrip("@")
        log(f"=== Instagram 専用スクレイピング開始: @{target_username} ===")

    if not check_dependencies():
        sys.exit(2)

    os.makedirs(output_dir, exist_ok=True)
    log(f"出力先: {output_dir}")

    session_dir = os.environ.get("IG_SESSION_DIR", "")
    if not session_dir:
        session_dir = os.path.join(os.getcwd(), "lib", "playwright", "instagram_session")
    os.makedirs(session_dir, exist_ok=True)

    L = _setup_instaloader()

    # 1. 保存済みセッションを試行
    logged_in_user = _try_load_session(L, session_dir)

    # 2. ブラウザCookie自動取得
    if not logged_in_user:
        log("ブラウザのCookieからセッションを取得するのだ...")
        logged_in_user = _try_import_browser_cookies(L, session_dir)

    # 3. フォールバック: stdin経由のインタラクティブログイン
    if not logged_in_user:
        log("ブラウザCookieからの取得に失敗したのだ。ログイン情報を入力してください。")
        logged_in_user = _login_interactive(L, session_dir)

    # メディアダウンロード
    try:
        if shortcode:
            download_single_post(L, shortcode, output_dir)
        else:
            download_media(L, target_username, output_dir)
    except SystemExit:
        raise
    except Exception as e:
        log(f"致命的エラー: {e}")
        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
