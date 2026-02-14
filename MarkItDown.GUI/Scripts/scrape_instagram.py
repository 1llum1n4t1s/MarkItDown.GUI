#!/usr/bin/env python3
"""
Instagram 専用スクレイピングスクリプト。
指定ユーザーの全投稿の画像・動画をダウンロードする。

認証フロー:
  1. 保存済みInstaloaderセッションがあれば検証
  2. Playwright persistent context でログイン確認
     - 未ログイン時はブラウザを表示して手動ログイン待機
     - Cookieを抽出して Instaloader にセット
  3. Instaloaderでメディアダウンロード

Usage:
    python scrape_instagram.py <target_username_or_url> <output_dir>

環境変数:
    IG_SESSION_DIR: セッションファイル保存ディレクトリ

終了コード:
    0: 正常完了
    1: 致命的エラー
    2: instaloader 未インストール
    3: セッション切れ（再ログインが必要）
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
#  Instaloaderセッション管理
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
#  Playwright persistent context でCookie取得
# ────────────────────────────────────────────

BROWSER_ARGS = [
    "--disable-blink-features=AutomationControlled",
    "--no-first-run",
    "--no-default-browser-check",
    "--disable-infobars",
    "--disable-extensions",
    "--disable-component-extensions-with-background-pages",
    "--disable-default-apps",
    "--disable-hang-monitor",
    "--disable-popup-blocking",
    "--metrics-recording-only",
    "--no-service-autorun",
    "--password-store=basic",
]


def _get_ig_user_data_dir(session_dir: str) -> str:
    """persistent context 用の User Data Directory を返す。"""
    profile_dir = os.path.join(session_dir, "ig_profile")
    os.makedirs(profile_dir, exist_ok=True)
    return profile_dir


def _launch_persistent(playwright_module, user_data_dir: str):
    """
    persistent context でブラウザを起動する。
    channel="chrome" を優先し、失敗時は Chromium にフォールバックする。
    """
    vp_width = 1280 + random.randint(-40, 40)
    vp_height = 900 + random.randint(-30, 30)
    launch_kwargs = dict(
        user_data_dir=user_data_dir,
        headless=False,
        args=BROWSER_ARGS,
        viewport={"width": vp_width, "height": vp_height},
        locale="ja-JP",
    )
    try:
        context = playwright_module.chromium.launch_persistent_context(
            channel="chrome",
            **launch_kwargs,
        )
        log("システムの Chrome (persistent) で起動したのだ")
    except Exception as e:
        log(f"Chrome での起動に失敗、Chromium にフォールバック: {e}")
        context = playwright_module.chromium.launch_persistent_context(
            **launch_kwargs,
        )
        log("Playwright Chromium (persistent) で起動したのだ")
    return context


def _collect_instagram_cookies(context) -> dict[str, str]:
    """Playwright context から Instagram Cookie を収集する。"""
    cookies = {}
    try:
        for cookie in context.cookies("https://www.instagram.com"):
            name = cookie.get("name", "")
            value = cookie.get("value", "")
            if name and value:
                cookies[name] = value
    except Exception as e:
        log(f"Cookie収集中にエラー: {e}")
    return cookies


def _is_logged_in(page, context) -> bool:
    """
    Instagram にログイン済みか判定する。
    sessionid Cookie と URL/DOM の両方を確認する。
    """
    current_url = page.url
    cookies = _collect_instagram_cookies(context)
    has_session = bool(cookies.get("sessionid"))
    if not has_session:
        return False
    if "/accounts/login" in current_url:
        return False
    try:
        login_input = page.query_selector('input[name="username"], input[name="password"]')
        if login_input:
            return False
    except Exception:
        pass
    return True


def ensure_login_via_playwright(session_dir: str) -> dict[str, str]:
    """
    Playwright persistent context でログイン確認し、必要なら手動ログインを待つ。
    Returns: Instagram Cookie dict
    """
    from playwright.sync_api import sync_playwright

    user_data_dir = _get_ig_user_data_dir(session_dir)
    context = None
    try:
        with sync_playwright() as p:
            log("保存済みプロファイルでInstagramセッション確認中なのだ（headedモード）...")
            context = _launch_persistent(p, user_data_dir)
            page = context.pages[0] if context.pages else context.new_page()
            page.goto("https://www.instagram.com/", wait_until="domcontentloaded", timeout=30000)
            time.sleep(5)

            if _is_logged_in(page, context):
                cookies = _collect_instagram_cookies(context)
                log("保存済みセッションが有効なのだ。")
                log(f"Instagram Cookieを {len(cookies)} 個取得したのだ。")
                return cookies

            log("未ログイン状態なのだ。ブラウザでInstagramにログインしてください...")
            page.goto("https://www.instagram.com/accounts/login/", wait_until="domcontentloaded", timeout=30000)
            log("ブラウザが開きました。Instagramにログインしてください...")
            log("ログイン完了を自動検知します。そのままお待ちください。")

            max_wait = 600
            elapsed = 0
            poll_interval = 3
            while elapsed < max_wait:
                time.sleep(poll_interval)
                elapsed += poll_interval
                try:
                    if _is_logged_in(page, context):
                        cookies = _collect_instagram_cookies(context)
                        log("ログイン完了を検知したのだ！")
                        log(f"Instagram Cookieを {len(cookies)} 個取得したのだ。")
                        return cookies
                except Exception as e:
                    log(f"ログイン状態確認中にエラー: {e}")
                if elapsed % 30 == 0:
                    log(f"ログイン待機中... ({elapsed}秒経過, URL: {page.url})")

            log("ログイン待機がタイムアウトしたのだ。(10分)")
            return {}
    except Exception as e:
        log(f"Playwrightログイン処理中にエラー: {e}")
        return {}
    finally:
        if context:
            try:
                context.close()
            except Exception as e:
                log(f"ブラウザコンテキストのクローズ中にエラー: {e}")


def _apply_cookies_to_instaloader(L, cookies: dict[str, str], session_dir: str) -> str | None:
    """
    Playwrightから取得したCookieをInstaloaderにセットし、セッションを検証・保存する。
    成功すればログインユーザー名を返す。
    """
    session_id = cookies.get("sessionid")
    if not session_id:
        log("Instagram のsessionidが見つからないのだ（ログインしていない可能性）。")
        return None

    log(f"ChromeのCookieをInstaloaderにセット中... (Cookie数: {len(cookies)})")

    # Instaloaderのセッションにcookieをセット
    L.context._session.cookies.update(cookies)

    # セッション検証
    test_user = L.test_login()
    if test_user:
        log(f"ChromeからInstaloaderへのセッション転送に成功: ユーザー = {test_user}")
        # セッションを保存（次回以降はPlaywright不要の可能性あり）
        session_file = _get_session_file(session_dir, test_user)
        L.context.username = test_user
        L.save_session_to_file(session_file)
        _save_session_info(session_dir, test_user)
        log("Instaloaderセッションを保存したのだ。")
        return test_user
    else:
        log("CookieをセットしたがInstaloaderのセッション検証に失敗したのだ。")
        return None


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

    # 1. 保存済みInstaloaderセッションを試行
    logged_in_user = _try_load_session(L, session_dir)

    # 2. Playwright persistent context でCookie取得 → Instaloaderにセット
    if not logged_in_user:
        log("PlaywrightブラウザでInstagramのCookieを取得するのだ...")
        cookies = ensure_login_via_playwright(session_dir)
        if cookies:
            logged_in_user = _apply_cookies_to_instaloader(L, cookies, session_dir)

    if not logged_in_user:
        log("ログインに失敗したのだ。")
        log("対処法: Chromeブラウザで https://www.instagram.com/ にアクセスしてログイン済みの状態にしてから、再度実行してください。")
        sys.exit(3)

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
