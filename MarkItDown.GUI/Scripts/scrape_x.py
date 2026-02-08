#!/usr/bin/env python3
"""
X.com (Twitter) 専用スクレイピングスクリプト。
指定ユーザーの全ツイートを検索コマンドで取得し、
オリジナル品質の画像をダウンロードする。

Usage:
    python scrape_x.py <username> <output_dir>

環境変数:
    X_SESSION_PATH: セッションファイルのパス

終了コード:
    0: 正常完了
    1: 致命的エラー
    2: playwright 未インストール
    3: セッション切れ（再ログインが必要）
"""

import json
import os
import random
import re
import sys
import time
import traceback
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import quote, urlparse, parse_qs, urlencode

def log(msg: str):
    """タイムスタンプ付きログ出力（C#側でアイドルタイムアウトをリセットする）"""
    print(f"[X.com] {msg}", flush=True)


def check_playwright():
    """playwright がインポートできるかチェック"""
    try:
        import playwright  # noqa: F401
        return True
    except ImportError:
        return False


def convert_image_url_to_orig(url: str) -> tuple[str, str]:
    """
    画像URLをオリジナル品質に変換する。
    Returns: (orig_url, format)

    例:
    https://pbs.twimg.com/media/XXXXX?format=jpg&name=small
    → https://pbs.twimg.com/media/XXXXX?format=jpg&name=orig
    """
    parsed = urlparse(url)
    params = parse_qs(parsed.query)

    fmt = params.get("format", ["jpg"])[0]
    # name=orig に変換
    params["name"] = ["orig"]
    if "format" in params:
        params["format"] = [fmt]

    new_query = urlencode({k: v[0] for k, v in params.items()})
    orig_url = f"{parsed.scheme}://{parsed.netloc}{parsed.path}?{new_query}"
    return orig_url, fmt


def extract_all_tweets_batch(page) -> list[dict]:
    """
    ページ上の全ツイート要素からデータを一括抽出する。
    1回の evaluate で全ツイートを処理することで、JSブリッジの往復回数を削減。
    """
    try:
        results = page.evaluate("""() => {
            const articles = document.querySelectorAll('article[data-testid="tweet"]');
            const tweets = [];

            function parseMetric(el) {
                if (!el) return 0;
                const span = el.querySelector('span[data-testid="app-text-transition-container"]');
                if (span) {
                    const text = span.innerText.trim();
                    if (!text) return 0;
                    const multipliers = {"K": 1000, "M": 1000000, "B": 1000000000};
                    const match = text.match(/^([\\d.]+)([KMB])?$/i);
                    if (match) {
                        const num = parseFloat(match[1]);
                        const mult = match[2] ? multipliers[match[2].toUpperCase()] : 1;
                        return Math.round(num * mult);
                    }
                    return parseInt(text.replace(/,/g, "")) || 0;
                }
                return 0;
            }

            for (const el of articles) {
                const result = {};

                const statusLink = el.querySelector('a[href*="/status/"]');
                if (statusLink) {
                    const match = statusLink.href.match(/\\/status\\/(\\d+)/);
                    if (match) result.tweet_id = match[1];
                }
                if (!result.tweet_id) continue;

                const textEl = el.querySelector('div[data-testid="tweetText"]');
                result.text = textEl ? textEl.innerText : "";

                const timeEl = el.querySelector("time");
                result.timestamp = timeEl ? timeEl.getAttribute("datetime") : null;

                const imgs = el.querySelectorAll('img[src*="pbs.twimg.com/media"]');
                result.images = [];
                for (const img of imgs) {
                    result.images.push(img.src);
                }

                result.metrics = {};
                const replyEl = el.querySelector('button[data-testid="reply"]');
                const retweetEl = el.querySelector('button[data-testid="retweet"]');
                const likeEl = el.querySelector('button[data-testid="like"], button[data-testid="unlike"]');

                result.metrics.replies = parseMetric(replyEl);
                result.metrics.retweets = parseMetric(retweetEl);
                result.metrics.likes = parseMetric(likeEl);

                const socialContext = el.querySelector('span[data-testid="socialContext"]');
                result.is_retweet = false;
                if (socialContext) {
                    const text = socialContext.innerText;
                    if (text.includes("reposted") || text.includes("リポスト")) {
                        result.is_retweet = true;
                    }
                }

                const replyTo = el.querySelector('div[id^="id__"]');
                result.is_reply = false;
                if (replyTo) {
                    const text = replyTo.innerText;
                    if (text.includes("Replying to") || text.includes("返信先")) {
                        result.is_reply = true;
                    }
                }

                tweets.push(result);
            }
            return tweets;
        }""")
        return results or []
    except Exception as e:
        log(f"ツイートデータ一括抽出エラー: {e}")
        return []


# Chromium 自動化検知回避用の起動オプション
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
    "--disable-prompt-on-repost",
    "--metrics-recording-only",
    "--no-service-autorun",
    "--password-store=basic",
]


def _get_user_data_dir(session_path: str) -> str:
    """
    persistent context 用の User Data Directory パスを返す。
    session_path の親ディレクトリに x_profile/ を作成する。
    """
    parent = os.path.dirname(session_path) or os.getcwd()
    profile_dir = os.path.join(parent, "x_profile")
    os.makedirs(profile_dir, exist_ok=True)
    return profile_dir


def _launch_persistent(playwright_module, user_data_dir: str, headless: bool):
    """
    persistent context でブラウザを起動する。
    Cookie/セッションはプロファイルディレクトリに自動保存される。
    channel="chrome" でシステム Chrome を使い、フォールバックで Chromium を使う。
    """
    # viewport サイズを少しランダム化（ブラウザフィンガープリント対策）
    vp_width = 1280 + random.randint(-40, 40)
    vp_height = 900 + random.randint(-30, 30)
    launch_kwargs = dict(
        user_data_dir=user_data_dir,
        headless=headless,
        args=BROWSER_ARGS,
        viewport={"width": vp_width, "height": vp_height},
        locale="ja-JP",
        # user_agent は指定しない（Chrome のデフォルト UA をそのまま使う）
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

    # navigator.webdriver を隠す（全ページで自動適用）
    _stealth_script = """
    Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
    // Chrome の plugins/languages を通常ブラウザと同等にする
    Object.defineProperty(navigator, 'plugins', {
        get: () => [1, 2, 3, 4, 5],
    });
    Object.defineProperty(navigator, 'languages', {
        get: () => ['ja-JP', 'ja', 'en-US', 'en'],
    });
    // Permissions API の通知ステータスを偽装
    const originalQuery = window.navigator.permissions.query;
    window.navigator.permissions.query = (parameters) =>
        parameters.name === 'notifications'
            ? Promise.resolve({ state: Notification.permission })
            : originalQuery(parameters);
    """
    context.add_init_script(_stealth_script)
    return context


def _is_logged_in(page) -> bool:
    """
    ページの状態からX.comにログイン済みかどうかを判定する。
    URLだけでなくDOM要素も確認する（headlessではリダイレクトされない場合がある）。
    """
    current_url = page.url
    log(f"ログイン状態確認中... URL: {current_url}")

    # 明らかにログインページの場合
    if "/login" in current_url or "/i/flow/login" in current_url:
        log("URLがログインページなのだ")
        return False

    # DOM でログイン状態を確認
    try:
        # ログイン済みの場合に存在する要素をチェック
        # アカウントメニュー（アバター）が存在するか
        account_button = page.query_selector(
            'button[data-testid="SideNav_AccountSwitcher_Button"], '
            'a[data-testid="AppTabBar_Home_Link"], '
            'nav[aria-label="Primary"], '
            'div[data-testid="primaryColumn"]'
        )
        if account_button:
            log("ログイン済みのDOM要素を検出したのだ")
            return True

        # 未ログイン時に表示される要素をチェック
        login_prompt = page.query_selector(
            'a[href="/login"], '
            'a[data-testid="loginButton"], '
            'div[data-testid="sheetDialog"], '
            'input[autocomplete="username"]'
        )
        if login_prompt:
            log("未ログインのDOM要素を検出したのだ")
            return False

        # ページタイトルで判断
        title = page.title()
        log(f"ページタイトル: {title}")
        if "ログイン" in title or "Login" in title or "Log in" in title:
            return False

        # 判定できない場合はURLで判断（/home なら一応OK）
        if "/home" in current_url:
            log("URLが /home のため、ログイン済みと仮判定するのだ")
            return True

        log("ログイン状態を判定できないため、未ログインとみなすのだ")
        return False

    except Exception as e:
        log(f"ログイン状態確認中にエラー: {e}")
        return False


def ensure_login(playwright_module, session_path: str) -> tuple:
    """
    persistent context でログイン済みか確認し、未ログインなら手動ログインを待つ。
    Returns: (context, page)
    ※ persistent context では browser と context が一体のため browser は返さない。
    """
    user_data_dir = _get_user_data_dir(session_path)

    # headed（画面表示）でセッション確認（BOT検知回避のため常に headed）
    log("保存済みプロファイルでセッション確認中なのだ（headedモード）...")
    context = _launch_persistent(playwright_module, user_data_dir, headless=False)
    page = context.pages[0] if context.pages else context.new_page()
    page.goto("https://x.com/home", wait_until="domcontentloaded", timeout=30000)
    time.sleep(5)  # X.comの初期ロードに十分な時間を確保

    if _is_logged_in(page):
        log("セッション復元に成功したのだ！")
        return context, page

    # 未ログイン → そのまま headed で手動ログイン待ち
    log("未ログイン状態なのだ。ブラウザでログインしてください...")
    context.close()

    return _manual_login(playwright_module, user_data_dir)


def _manual_login(playwright_module, user_data_dir: str) -> tuple:
    """
    ヘッドありブラウザを起動し、ユーザーの手動ログインを待機する。
    ログイン完了をDOM検査で自動検知して処理を進める。
    Returns: (context, page)
    """
    log("ブラウザを起動中なのだ... X.comにログインしてください。")
    context = _launch_persistent(playwright_module, user_data_dir, headless=False)
    page = context.pages[0] if context.pages else context.new_page()
    page.goto("https://x.com/i/flow/login", wait_until="domcontentloaded", timeout=30000)

    log("ブラウザが開きました。X.comにログインしてください...")
    log("ログイン完了を自動検知します。そのままお待ちください。")

    # ログイン完了をポーリングで検知（最大10分待機）
    max_wait = 600  # 10分（2段階認証等を考慮）
    elapsed = 0
    poll_interval = 3  # 3秒間隔で高速ポーリング

    while elapsed < max_wait:
        time.sleep(poll_interval)
        elapsed += poll_interval

        try:
            current_url = page.url
        except Exception:
            # ブラウザが閉じられた場合
            log("ブラウザが閉じられたのだ。処理を中断するのだ。")
            sys.exit(1)

        # 方法1: URLが明確にログイン後のページになった
        if "/login" not in current_url and "/i/flow" not in current_url:
            log(f"ログインページ以外に遷移を検知: {current_url}")
            # 少し待ってからDOM確認（ページ読み込み完了を待つ）
            time.sleep(2)
            if _is_logged_in(page):
                log(f"ログイン完了を検知したのだ！ URL: {current_url}")
                break

        # 方法2: URLがログインページのままでもDOMにログイン済み要素が出現
        # （X.comのSPAがURLを変えずにDOMを書き換えるケース）
        try:
            logged_in_el = page.query_selector(
                'a[data-testid="AppTabBar_Home_Link"], '
                'button[data-testid="SideNav_AccountSwitcher_Button"], '
                'a[href="/home"][role="link"]'
            )
            if logged_in_el:
                log(f"ログイン済みDOM要素を検知したのだ！ URL: {current_url}")
                break
        except Exception:
            pass

        if elapsed % 30 == 0:
            log(f"ログイン待機中... ({elapsed}秒経過, URL: {current_url})")

    else:
        log("ログイン待機がタイムアウトしたのだ。(10分)")
        context.close()
        sys.exit(1)

    log("ログイン成功！セッションはプロファイルに自動保存されるのだ。")

    # そのまま headed で続行（BOT検知回避のため headless に切り替えない）
    return context, page


def _check_loading(page) -> bool:
    """ページが読み込み中かどうかを判定する"""
    try:
        # ローディングスピナー（プログレスバー）の検出
        spinner = page.query_selector(
            'div[role="progressbar"], '
            'svg circle[r="10"], '  # X.comのローディングアニメーション
            'div[data-testid="cellInnerDiv"] > div[style*="height: 0"]'
        )
        return spinner is not None and spinner.is_visible()
    except Exception:
        return False


def _human_scroll(page, distance: float = 1500):
    """
    複数のスクロール手段をランダムに組み合わせる（BOT検知回避）。
    同一パターンの繰り返しを避けることで、自動化の指紋を残さない。
    headed モードなので全手段が自然に動作する。
    """
    presses = max(1, round(distance / 900))
    # 毎回ランダムに手段を選択
    method = random.choice(["pagedown", "space", "wheel", "arrow"])

    if method == "pagedown":
        page.keyboard.press("Escape")
        for i in range(presses):
            page.keyboard.press("PageDown")
            if i < presses - 1:
                time.sleep(random.uniform(0.02, 0.06))

    elif method == "space":
        page.keyboard.press("Escape")
        for i in range(presses):
            page.keyboard.press("Space")
            if i < presses - 1:
                time.sleep(random.uniform(0.02, 0.06))

    elif method == "wheel":
        page.mouse.move(
            random.randint(400, 900),
            random.randint(300, 600),
        )
        remaining = distance
        while remaining > 0:
            step = min(remaining, random.uniform(400, 800))
            page.mouse.wheel(0, step)
            remaining -= step
            if remaining > 0:
                time.sleep(random.uniform(0.01, 0.04))

    elif method == "arrow":
        # ArrowDown 連打（1回 ≒ 40px、細かいがパターンが異なる）
        page.keyboard.press("Escape")
        arrow_count = max(5, round(distance / 40))
        for i in range(arrow_count):
            page.keyboard.press("ArrowDown")
            # 数回に1回だけ微小ウェイト
            if i % 5 == 4:
                time.sleep(random.uniform(0.005, 0.02))


def _handle_interruptions(page):
    """
    スクロール中に発生する各種ボタンを検出して対処する。
    - 「再試行」ボタン
    - 「もっと見る」ボタン
    エラー表示は検出しない（新規0件で即座に迂回＋再検索するため）。
    """
    try:
        # 「再試行 / Retry」ボタン
        retry_button = page.query_selector(
            'div[role="button"]:has-text("Retry"), '
            'div[role="button"]:has-text("再試行"), '
            'button:has-text("Retry"), '
            'button:has-text("再試行")'
        )
        if retry_button and retry_button.is_visible():
            retry_button.click()
            log("再試行ボタンを押したのだ")
            time.sleep(3)
            return

        # 「もっと見る / Show more」的なボタン
        show_more = page.query_selector(
            'div[role="button"]:has-text("Show"), '
            'div[role="button"]:has-text("もっと見る"), '
            'div[role="button"]:has-text("表示")'
        )
        if show_more and show_more.is_visible():
            show_more.click()
            log("もっと見るボタンを押したのだ")
            time.sleep(3)
            return

    except Exception:
        pass


def scrape_tweets(page, username: str) -> list[dict]:
    """
    Phase 1: from:username 検索で全ツイートを取得する。
    無限スクロールで最古まで到達。
    新規ツイートが取得できなくなったら即座に別ページを巡回し、
    until: 付き検索で再開することでBOT対策を回避する。
    """
    tweets = {}  # tweet_id → tweet_data
    base_search_query = f"from:{username} -filter:retweets"
    search_url = f"https://x.com/search?q={quote(base_search_query)}&src=typed_query&f=live"

    def _build_search_url(until_date: str | None = None) -> str:
        """検索URLを生成する。until_date があれば until: パラメータを付与。"""
        if until_date:
            q = f"from:{username} -filter:retweets until:{until_date}"
        else:
            q = base_search_query
        return f"https://x.com/search?q={quote(q)}&src=typed_query&f=live"

    def _get_oldest_tweet_date() -> str | None:
        """取得済みツイートの中で最も古いタイムスタンプの翌日を返す（until: 用）。"""
        oldest_ts = None
        for t in tweets.values():
            ts = t.get("timestamp")
            if ts:
                try:
                    dt = datetime.fromisoformat(ts.replace("Z", "+00:00"))
                    if oldest_ts is None or dt < oldest_ts:
                        oldest_ts = dt
                except Exception:
                    pass
        if oldest_ts:
            # until は「その日を含まない」ので +1日
            from datetime import timedelta
            until_dt = oldest_ts + timedelta(days=1)
            return until_dt.strftime("%Y-%m-%d")
        return None

    log(f"検索URL: {search_url}")
    page.goto(search_url, wait_until="domcontentloaded", timeout=30000)
    time.sleep(5)  # 検索結果の読み込みに十分な時間を確保

    # リダイレクト先URLをログ
    log(f"検索後のURL: {page.url}")

    # ログインページにリダイレクトされた場合
    if "/login" in page.url or "/i/flow/login" in page.url:
        log("セッションが切れているのだ。再ログインが必要なのだ。")
        sys.exit(3)

    # ログイン状態を再確認（DOM検査）
    if not _is_logged_in(page):
        log("検索ページでログインが確認できないのだ。再ログインが必要なのだ。")
        sys.exit(3)

    # ページの状態をデバッグログ
    try:
        page_title = page.title()
        log(f"ページタイトル: {page_title}")
        # 検索結果が表示されているか確認
        body_text = page.evaluate("() => document.body ? document.body.innerText.substring(0, 500) : '(empty)'")
        log(f"ページ先頭テキスト: {body_text[:200]}")
    except Exception as e:
        log(f"ページデバッグ情報取得エラー: {e}")

    scroll_count = 0
    no_new_count = 0
    last_save_count = 0
    renavigate_count = 0     # 迂回＋再検索した回数
    max_renavigate = 5       # 連続で新規0のまま迂回した上限（＝本当の末端）
    prev_total = 0           # 前回の迂回時の合計件数（進捗チェック用）

    def _detour_and_resume():
        """別ページを巡回し、until: 付き検索で再開する。"""
        nonlocal renavigate_count, search_url, scroll_count, no_new_count, prev_total
        renavigate_count += 1
        if renavigate_count > max_renavigate:
            log(f"迂回上限({max_renavigate}回)に到達。完了とするのだ。")
            return False
        # 前回迂回時から1件も増えていなければ本当の末端
        if renavigate_count > 1 and len(tweets) == prev_total:
            log("前回の迂回から新規ツイートが増えていないのだ。末端に到達したと判断するのだ。")
            return False
        prev_total = len(tweets)
        log(f"新規ツイートなし。別ページを巡回して再検索するのだ（{renavigate_count}/{max_renavigate}）")
        # 複数の別ページを順番に巡回（人間的な行動を模倣）
        detour_pages = [
            "https://x.com/home",
            "https://x.com/explore",
            "https://x.com/explore/tabs/trending",
            "https://x.com/notifications",
        ]
        for detour_url in detour_pages:
            try:
                log(f"  → {detour_url}")
                page.goto(detour_url, wait_until="domcontentloaded", timeout=30000)
                time.sleep(random.uniform(2, 4))
                _human_scroll(page, random.uniform(800, 1500))
                time.sleep(random.uniform(1, 3))
            except Exception as e:
                log(f"  迂回ページ遷移エラー（無視して続行）: {e}")
        # until: 付き検索URLで再開（既取得分のスクロールが不要になる）
        until_date = _get_oldest_tweet_date()
        search_url = _build_search_url(until_date)
        if until_date:
            log(f"until:{until_date} 付きで検索を再開するのだ...")
        else:
            log("日付情報がないため、通常検索で再開するのだ...")
        page.goto(search_url, wait_until="domcontentloaded", timeout=30000)
        time.sleep(random.uniform(4, 7))
        scroll_count = 0
        no_new_count = 0
        return True

    while True:
        scroll_count += 1

        # 全ツイートを一括抽出（1回のJS実行で全件処理）
        all_tweet_data = extract_all_tweets_batch(page)

        # 初回スクロールで要素がない場合のデバッグ
        if scroll_count == 1 and len(all_tweet_data) == 0:
            log("初回スクロールでツイート要素が見つからないのだ。ページ構造を確認中...")
            try:
                error_text = page.evaluate("""() => {
                    const el = document.querySelector('[data-testid="empty_state_header_text"], [data-testid="error-detail"]');
                    return el ? el.innerText : null;
                }""")
                if error_text:
                    log(f"エラーメッセージ検出: {error_text}")
            except Exception as e:
                log(f"デバッグ情報取得エラー: {e}")

        new_count = 0
        for data in all_tweet_data:
            if data and data.get("tweet_id"):
                tid = data["tweet_id"]
                if tid not in tweets:
                    tweets[tid] = data
                    new_count += 1

        if new_count > 0:
            no_new_count = 0
            renavigate_count = 0  # 正常取得できたら迂回カウントもリセット
        else:
            no_new_count += 1

        log(f"スクロール #{scroll_count}, 新規: {new_count}, 取得ツイート合計: {len(tweets)}")

        # 新規0件が3回続いたら即座に迂回＋再検索
        if no_new_count >= 3:
            log(f"連続{no_new_count}回新規ツイートなし。")
            if not _detour_and_resume():
                break
            continue

        # 100件ごとに中間ログ
        if len(tweets) - last_save_count >= 100:
            log(f"--- {len(tweets)} 件のツイートを取得済み ---")
            last_save_count = len(tweets)

        # スクロール（マウスホイールでBOT検知を回避）
        _human_scroll(page, random.uniform(1500, 2500))

        # コンテンツ読み込み待機（短め）
        time.sleep(random.uniform(0.5, 1.0))

        # ボタン対処（再試行/もっと見る等）
        _handle_interruptions(page)

    return list(tweets.values())


def download_images(tweets: list[dict], output_dir: str) -> int:
    """
    Phase 2: 全ツイートの画像をオリジナル品質でダウンロードする。
    httpx を使用して順次ダウンロード。
    """
    import httpx

    # 画像URL一覧を収集（リツイート/リポストの画像は除外）
    image_tasks = []  # (tweet_id, index, orig_url, format, filename)
    skipped_retweets = 0
    for tweet in tweets:
        if not tweet.get("images"):
            continue
        if tweet.get("is_retweet", False):
            skipped_retweets += 1
            continue
        for idx, img_url in enumerate(tweet["images"]):
            orig_url, fmt = convert_image_url_to_orig(img_url)
            filename = f"{tweet['tweet_id']}_{idx}.{fmt}"
            image_tasks.append((tweet["tweet_id"], idx, orig_url, fmt, filename))

    total = len(image_tasks)
    if skipped_retweets > 0:
        log(f"リポスト {skipped_retweets} 件の画像をスキップしたのだ")
    if total == 0:
        log("ダウンロードする画像がないのだ。")
        return 0

    log(f"画像ダウンロード開始: {total} 枚")
    downloaded = 0
    failed = 0

    with httpx.Client(
        timeout=60.0,
        follow_redirects=True,
        headers={
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            "Referer": "https://x.com/"
        }
    ) as client:
        for i, (tweet_id, idx, orig_url, fmt, filename) in enumerate(image_tasks):
            filepath = os.path.join(output_dir, filename)

            # 既にダウンロード済みならスキップ
            if os.path.exists(filepath) and os.path.getsize(filepath) > 0:
                log(f"画像DL {i + 1}/{total}: {filename} (スキップ: 既存)")
                downloaded += 1
                # ツイートデータの画像情報を更新
                _update_tweet_image_info(tweets, tweet_id, idx, orig_url, filename, True)
                continue

            success = False
            for retry in range(3):
                try:
                    response = client.get(orig_url)
                    response.raise_for_status()

                    with open(filepath, "wb") as f:
                        f.write(response.content)

                    downloaded += 1
                    success = True
                    log(f"画像DL {i + 1}/{total}: {filename} ({len(response.content) / 1024:.1f} KB)")

                    # ツイートデータの画像情報を更新
                    _update_tweet_image_info(tweets, tweet_id, idx, orig_url, filename, True)
                    break

                except Exception as e:
                    if retry < 2:
                        log(f"画像DL リトライ {retry + 1}/3: {filename} ({e})")
                        time.sleep(2)
                    else:
                        log(f"画像DL 失敗: {filename} ({e})")
                        failed += 1
                        _update_tweet_image_info(tweets, tweet_id, idx, orig_url, filename, False)

            # レートリミット対策: 少し待機
            if success:
                time.sleep(0.5)

    log(f"画像ダウンロード完了: {downloaded}/{total} 成功, {failed} 失敗")
    return downloaded


def _update_tweet_image_info(tweets: list[dict], tweet_id: str, idx: int, orig_url: str, filename: str, downloaded: bool):
    """ツイートデータ内の画像情報をダウンロード結果で更新する"""
    for tweet in tweets:
        if tweet.get("tweet_id") == tweet_id:
            if "image_details" not in tweet:
                tweet["image_details"] = []

            # 既に同じインデックスのエントリがあれば更新
            found = False
            for detail in tweet["image_details"]:
                if detail.get("index") == idx:
                    detail["url"] = orig_url
                    detail["filename"] = filename
                    detail["downloaded"] = downloaded
                    found = True
                    break

            if not found:
                tweet["image_details"].append({
                    "index": idx,
                    "url": orig_url,
                    "filename": filename,
                    "downloaded": downloaded
                })
            break


def save_json(tweets: list[dict], username: str, output_dir: str) -> str:
    """最終JSONを出力する"""
    output_path = os.path.join(output_dir, f"{username}.json")

    # 画像カウント
    image_count = 0
    for tweet in tweets:
        details = tweet.get("image_details", [])
        image_count += sum(1 for d in details if d.get("downloaded", False))

    # JSON出力データ構築
    output_data = {
        "username": username,
        "url": f"https://x.com/{username}",
        "scraped_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "tweet_count": len(tweets),
        "image_count": image_count,
        "tweets": []
    }

    for tweet in sorted(tweets, key=lambda t: t.get("tweet_id", ""), reverse=True):
        tweet_entry = {
            "tweet_id": tweet.get("tweet_id", ""),
            "text": tweet.get("text", ""),
            "timestamp": tweet.get("timestamp"),
            "is_retweet": tweet.get("is_retweet", False),
            "is_reply": tweet.get("is_reply", False),
            "images": [],
            "metrics": tweet.get("metrics", {})
        }

        # 画像詳細をimages配列に変換
        for detail in tweet.get("image_details", []):
            tweet_entry["images"].append({
                "url": detail.get("url", ""),
                "filename": detail.get("filename", ""),
                "downloaded": detail.get("downloaded", False)
            })

        output_data["tweets"].append(tweet_entry)

    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(output_data, f, ensure_ascii=False, indent=2)

    log(f"JSON出力完了: {output_path}")
    return output_path


def main():
    if len(sys.argv) < 3:
        print("Usage: python scrape_x.py <username> <output_dir>", file=sys.stderr)
        sys.exit(1)

    username = sys.argv[1]
    output_dir = sys.argv[2]

    # ユーザー名から@ を除去（念のため）
    username = username.lstrip("@")

    log(f"=== X.com 専用スクレイピング開始: @{username} ===")

    if not check_playwright():
        log("playwright パッケージがインストールされていないのだ")
        sys.exit(2)

    # 出力ディレクトリ準備（C#側で username サブフォルダは作成済み）
    user_output_dir = output_dir
    os.makedirs(user_output_dir, exist_ok=True)
    log(f"出力先: {user_output_dir}")

    session_path = os.environ.get("X_SESSION_PATH", "")
    if not session_path:
        session_path = os.path.join(os.getcwd(), "lib", "playwright", "x_session.json")

    from playwright.sync_api import sync_playwright

    context = None
    try:
        with sync_playwright() as p:
            context, page = ensure_login(p, session_path)

            # Phase 1: 全ツイート取得
            log("=== Phase 1: ツイート取得 ===")
            tweets = scrape_tweets(page, username)
            log(f"ツイート取得完了: {len(tweets)} 件")

            if len(tweets) == 0:
                log("ツイートが見つからなかったのだ。ユーザー名を確認してください。")
                # 空のJSONを出力
                save_json(tweets, username, user_output_dir)
                context.close()
                context = None
                return

            # 中間JSON保存
            save_json(tweets, username, user_output_dir)
            log("中間JSONを保存したのだ")

            # ブラウザを閉じる（画像DLにはブラウザ不要）
            context.close()
            context = None
            log("ブラウザを閉じたのだ")

            # Phase 2: 画像ダウンロード
            log("=== Phase 2: 画像ダウンロード ===")
            download_images(tweets, user_output_dir)

            # 最終JSON保存（画像DL結果を反映）
            final_path = save_json(tweets, username, user_output_dir)
            log(f"=== 完了! ツイート: {len(tweets)} 件, 出力: {final_path} ===")

    except SystemExit:
        raise
    except Exception as e:
        log(f"致命的エラー: {e}")
        traceback.print_exc()
        sys.exit(1)
    finally:
        if context:
            try:
                context.close()
            except Exception:
                pass


if __name__ == "__main__":
    main()
