"""
Playwright + Claude Code CLI を使用した AI ガイド型 Web スクレイピングスクリプト。

Claude Code CLI にページ構造を分析させ、最適な CSS セレクタと抽出戦略を
動的に決定してスクレイピングを行う。

Usage:
    python scrape_page.py <url> <output_path>

環境変数:
    CLAUDE_NODE_PATH - Node.js の実行パス
    CLAUDE_CLI_PATH  - Claude Code CLI の実行パス

出力: JSON形式のページデータ
"""

import json
import os
import re
import subprocess
import sys
import threading
import time
import traceback
from urllib.parse import urljoin, urlparse

try:
    from playwright.sync_api import sync_playwright
except ImportError:
    sync_playwright = None


def log(msg: str):
    print(msg, flush=True)


def install_playwright_browsers():
    """Playwright のブラウザバイナリをインストールする (chromium のみ)"""
    log("Playwright ブラウザをインストール中...")
    result = subprocess.run(
        [sys.executable, "-m", "playwright", "install", "chromium"],
        capture_output=True, text=True, timeout=300
    )
    if result.returncode != 0:
        log(f"Playwright ブラウザインストールエラー: {result.stderr}")
        raise RuntimeError("Playwright ブラウザのインストールに失敗しました")
    log("Playwright ブラウザのインストール完了")


def check_playwright_browsers() -> bool:
    """Playwright の chromium ブラウザが利用可能かチェックする"""
    if sync_playwright is None:
        return False
    try:
        with sync_playwright() as p:
            browser = p.chromium.launch(headless=True)
            browser.close()
        return True
    except Exception:
        return False


# ────────────────────────────────────────────
#  Claude Code CLI ページ分析・戦略生成
# ────────────────────────────────────────────

def get_page_summary(page, url: str) -> dict:
    """
    ページ構造のサマリーを生成する（Claude に送信するためのコンパクトな情報）。
    HTMLそのものは大きすぎるため、DOM統計とサンプルを送る。
    """
    summary = {
        "url": url,
        "title": page.title() or "",
        "sample_html": "",
        "dom_stats": {},
        "visible_text_sample": "",
    }

    # DOM統計をJavaScriptで取得
    try:
        dom_stats = page.evaluate("""() => {
            // タグ出現数
            const tagCounts = {};
            document.querySelectorAll('*').forEach(el => {
                const tag = el.tagName.toLowerCase();
                tagCounts[tag] = (tagCounts[tag] || 0) + 1;
            });

            // class名の出現頻度 Top 20
            const classCounts = {};
            document.querySelectorAll('[class]').forEach(el => {
                el.classList.forEach(cls => {
                    if (cls.length > 1 && cls.length < 50) {
                        classCounts[cls] = (classCounts[cls] || 0) + 1;
                    }
                });
            });
            const topClasses = Object.entries(classCounts)
                .sort((a, b) => b[1] - a[1])
                .slice(0, 20)
                .map(([cls, count]) => `${cls}(${count})`);

            // id一覧 Top 20
            const ids = [];
            document.querySelectorAll('[id]').forEach(el => {
                if (el.id && el.id.length < 50) ids.push(el.id);
            });

            return {
                total_elements: document.querySelectorAll('*').length,
                tag_counts: tagCounts,
                common_classes: topClasses,
                ids: ids.slice(0, 20)
            };
        }""")
        summary["dom_stats"] = dom_stats
    except Exception as e:
        log(f"DOM統計取得エラー: {e}")

    # HTMLサンプル（script/style除去、先頭4000文字）
    try:
        sample_html = page.evaluate("""() => {
            const clone = document.documentElement.cloneNode(true);
            clone.querySelectorAll('script, style, noscript, svg, iframe').forEach(el => el.remove());
            let html = clone.outerHTML;
            return html.substring(0, 4000);
        }""")
        summary["sample_html"] = sample_html
    except Exception as e:
        log(f"HTMLサンプル取得エラー: {e}")

    # 可視テキストのサンプル（先頭500文字）
    try:
        visible_text = page.evaluate("""() => {
            return document.body.innerText?.substring(0, 500) || '';
        }""")
        summary["visible_text_sample"] = visible_text
    except Exception:
        pass

    return summary


def claude_call(prompt, stdin_text=""):
    """Claude Code CLI を呼び出してレスポンスを取得する"""
    node_path = os.environ.get("CLAUDE_NODE_PATH", "")
    cli_path = os.environ.get("CLAUDE_CLI_PATH", "")
    if not node_path or not cli_path:
        return None
    try:
        env = {**os.environ, "CI": "true"}
        result = subprocess.run(
            [node_path, cli_path, "-p", prompt],
            input=stdin_text, capture_output=True, text=True,
            timeout=300, env=env
        )
        if result.returncode == 0:
            return result.stdout.strip()
        else:
            log(f"Claude CLI エラー (exit {result.returncode}): {result.stderr[:200]}")
            return None
    except subprocess.TimeoutExpired:
        log("Claude CLI がタイムアウトしました")
        return None
    except Exception as e:
        log(f"Claude CLI 呼び出しエラー: {e}")
        return None


def generate_scraping_strategy(page_summary: dict) -> dict:
    """
    Claude Code CLI にページ構造を分析させ、スクレイピング戦略 JSON を返す。
    """
    prompt = build_strategy_prompt(page_summary)

    # 推論中にハートビートを出力してC#側のアイドルタイムアウトを回避
    stop_heartbeat = threading.Event()

    def heartbeat():
        elapsed = 0
        while not stop_heartbeat.wait(30):
            elapsed += 30
            log(f"Claude 推論中... ({elapsed}秒経過)")

    hb_thread = threading.Thread(target=heartbeat, daemon=True)
    hb_thread.start()

    try:
        log("Claude Code CLI にスクレイピング戦略を問い合わせ中...")
        response_text = claude_call(prompt)
    finally:
        stop_heartbeat.set()
        hb_thread.join(timeout=1)

    if not response_text:
        raise ValueError("Claude Code CLI から応答を取得できませんでした")

    log(f"Claude 応答長: {len(response_text)} 文字")

    strategy_json = extract_json_from_text(response_text)
    if not strategy_json:
        log(f"Claude の応答から有効な JSON を抽出できませんでした: {response_text[:200]}")
        raise ValueError("Claude Code CLI から有効なスクレイピング戦略を取得できませんでした")

    strategy = json.loads(strategy_json)
    log(f"戦略生成完了: page_type={strategy.get('page_type', 'unknown')}")
    return strategy


def build_strategy_prompt(page_summary: dict) -> str:
    """Claude Code CLI に送るスクレイピング戦略生成プロンプトを構築する"""
    dom_stats = json.dumps(page_summary.get("dom_stats", {}), ensure_ascii=False, indent=2)

    return f"""以下の Web ページの構造を分析し、最適なスクレイピング戦略を JSON で返してください。

【ページ情報】
URL: {page_summary['url']}
タイトル: {page_summary['title']}

【DOM統計】
{dom_stats}

【HTMLサンプル（先頭4000文字）】
{page_summary.get('sample_html', '')}

【可視テキストサンプル（先頭500文字）】
{page_summary.get('visible_text_sample', '')}

【指示】
以下の項目を分析し、JSON で返してください:
1. page_type: ページの種類（blog_listing, article, product_page, forum_thread, news, portfolio, generic のいずれか）
2. content_selectors: メインコンテンツ領域の CSS セレクタ
   - main_container: メインコンテンツを囲む要素（例: "article.post", "main", "#content"）
   - title: タイトル要素（例: "h1.entry-title"）
   - body: 本文要素（例: ".entry-content", ".post-body"）
   - author: 著者要素（あれば）
   - date: 日付要素（あれば）
   - items: リストページの場合、各アイテムのセレクタ（例: ".post-item", "article"）
3. pagination: ページネーション情報
   - next_selector: 次のページへのリンクの CSS セレクタ（なければ null）
4. ignore_selectors: 無視すべき要素の CSS セレクタ配列（ナビ、広告、サイドバー等）
5. extraction_fields: 抽出するフィールド定義の配列
   - name: フィールド名
   - selector: CSS セレクタ
   - attribute: 取得する属性（省略時は innerText）

【出力形式】
以下の JSON フォーマットのみを出力してください:
{{
  "page_type": "blog_listing",
  "content_selectors": {{
    "main_container": "main",
    "title": "h1",
    "body": ".content",
    "author": ".author",
    "date": "time",
    "items": "article"
  }},
  "pagination": {{
    "next_selector": "a.next"
  }},
  "ignore_selectors": ["nav", "footer", ".sidebar", ".ad"],
  "extraction_fields": [
    {{"name": "title", "selector": "h1"}},
    {{"name": "content", "selector": ".content"}},
    {{"name": "date", "selector": "time", "attribute": "datetime"}}
  ]
}}"""


def extract_json_from_text(text: str) -> str | None:
    """テキストから JSON 部分を抽出する"""
    # ```json ... ``` ブロックを優先
    m = re.search(r"```(?:json)?\s*\n?(.*?)\n?```", text, re.DOTALL)
    if m:
        candidate = m.group(1).strip()
        try:
            json.loads(candidate)
            return candidate
        except json.JSONDecodeError:
            pass

    # JSON オブジェクトまたは配列をスタックベースで検出
    first_brace = text.find('{')
    first_bracket = text.find('[')

    if first_brace == -1 and first_bracket == -1:
        return None

    # 先に出現する開始文字を特定
    if first_brace != -1 and (first_bracket == -1 or first_brace < first_bracket):
        start_pos = first_brace
        open_char = '{'
        close_char = '}'
    else:
        start_pos = first_bracket
        open_char = '['
        close_char = ']'

    # スタックで対応する閉じ文字を探す（文字列リテラル内を考慮）
    depth = 0
    in_string = False
    escape_next = False
    end_pos = -1

    for i in range(start_pos, len(text)):
        c = text[i]
        if escape_next:
            escape_next = False
            continue
        if c == '\\' and in_string:
            escape_next = True
            continue
        if c == '"' :
            in_string = not in_string
            continue
        if in_string:
            continue
        if c == open_char:
            depth += 1
        elif c == close_char:
            depth -= 1
            if depth == 0:
                end_pos = i
                break

    if end_pos > start_pos:
        candidate = text[start_pos:end_pos + 1]
        try:
            json.loads(candidate)
            return candidate
        except json.JSONDecodeError:
            pass

    return None


# ────────────────────────────────────────────
#  戦略ベースのデータ抽出
# ────────────────────────────────────────────

def extract_with_strategy(page, url: str, strategy: dict) -> dict:
    """
    Claude が生成した戦略に基づいてページデータを抽出する。
    """
    data = {
        "url": url,
        "title": page.title() or "",
        "page_type": strategy.get("page_type", "generic"),
        "metadata": {},
        "structured_data": None,
        "content": [],
        "images": [],
        "links": [],
    }

    ignore_selectors = strategy.get("ignore_selectors", [])
    content_selectors = strategy.get("content_selectors", {})
    extraction_fields = strategy.get("extraction_fields", [])

    # メタデータ
    extract_metadata(page, data)

    # JSON-LD
    extract_json_ld(page, data)

    # 戦略ベースのコンテンツ抽出
    items_selector = content_selectors.get("items")
    main_container = content_selectors.get("main_container")

    if items_selector:
        # リストページ（ブログ一覧等）: 各アイテムからフィールドを抽出
        extract_list_items(page, items_selector, extraction_fields, ignore_selectors, data)
    elif main_container:
        # 単一記事ページ: メインコンテナからフィールドを抽出
        extract_from_container(page, main_container, extraction_fields, ignore_selectors, data)
    else:
        # フィールド定義のみで抽出
        extract_fields_global(page, extraction_fields, ignore_selectors, data)

    # 画像
    extract_images(page, url, ignore_selectors, data)

    # リンク
    extract_links(page, url, ignore_selectors, data)

    content_count = len(data.get("content", []))
    if content_count == 0:
        raise RuntimeError(
            f"戦略ベースの抽出でコンテンツが見つかりませんでした。"
            f"戦略: {json.dumps(strategy, ensure_ascii=False)[:300]}"
        )

    return data


def extract_metadata(page, data: dict):
    """メタタグを抽出する"""
    try:
        metas = page.query_selector_all("meta[name], meta[property]")
        for m in metas:
            name = m.get_attribute("name") or m.get_attribute("property") or ""
            content = m.get_attribute("content") or ""
            if name and content:
                data["metadata"][name] = content
    except Exception:
        pass


def extract_json_ld(page, data: dict):
    """JSON-LD 構造化データを抽出する"""
    try:
        ld_elements = page.query_selector_all('script[type="application/ld+json"]')
        ld_list = []
        for ld_el in ld_elements:
            try:
                ld_text = ld_el.inner_text()
                if ld_text:
                    ld_list.append(json.loads(ld_text))
            except Exception:
                pass
        if len(ld_list) == 1:
            data["structured_data"] = ld_list[0]
        elif len(ld_list) > 1:
            data["structured_data"] = ld_list
    except Exception:
        pass


def extract_list_items(page, items_selector: str, fields: list, ignore: list, data: dict):
    """リストページから各アイテムを抽出する（ブログ一覧等）"""
    try:
        items = page.query_selector_all(items_selector)
        log(f"アイテム数: {len(items)} (セレクタ: {items_selector})")

        for i, item in enumerate(items):
            # 無視セレクタに含まれる要素をスキップ
            if should_ignore(item, ignore):
                continue

            item_data = {}
            for field in fields:
                name = field.get("name", "")
                selector = field.get("selector", "")
                attribute = field.get("attribute")

                if not name or not selector:
                    continue

                try:
                    el = item.query_selector(selector)
                    if el:
                        if attribute:
                            value = el.get_attribute(attribute) or ""
                        else:
                            value = (el.inner_text() or "").strip()
                        if value:
                            item_data[name] = value
                except Exception:
                    continue

            # フィールドが取得できなかった場合、アイテム全体のテキストを取得
            if not item_data:
                try:
                    text = (item.inner_text() or "").strip()
                    if text and len(text) >= 3:
                        item_data["text"] = text
                except Exception:
                    continue

            if item_data:
                data["content"].append(item_data)

    except Exception as e:
        log(f"リストアイテム抽出エラー: {e}")


def extract_from_container(page, container_selector: str, fields: list, ignore: list, data: dict):
    """メインコンテナからフィールドを抽出する"""
    try:
        container = page.query_selector(container_selector)
        if not container:
            log(f"メインコンテナが見つかりません: {container_selector}")
            # グローバルにフォールバック
            extract_fields_global(page, fields, ignore, data)
            return

        content_item = {}
        for field in fields:
            name = field.get("name", "")
            selector = field.get("selector", "")
            attribute = field.get("attribute")

            if not name or not selector:
                continue

            try:
                el = container.query_selector(selector)
                if el and not should_ignore(el, ignore):
                    if attribute:
                        value = el.get_attribute(attribute) or ""
                    else:
                        value = (el.inner_text() or "").strip()
                    if value:
                        content_item[name] = value
            except Exception:
                continue

        if content_item:
            data["content"].append(content_item)
        else:
            # フィールド抽出に失敗した場合、コンテナ全体のテキストを取得
            try:
                text = (container.inner_text() or "").strip()
                if text:
                    data["content"].append({"text": text})
            except Exception:
                pass

    except Exception as e:
        log(f"コンテナ抽出エラー: {e}")


def extract_fields_global(page, fields: list, ignore: list, data: dict):
    """ページ全体からフィールドを抽出する"""
    content_item = {}
    for field in fields:
        name = field.get("name", "")
        selector = field.get("selector", "")
        attribute = field.get("attribute")

        if not name or not selector:
            continue

        try:
            elements = page.query_selector_all(selector)
            values = []
            for el in elements:
                if should_ignore(el, ignore):
                    continue
                if attribute:
                    v = el.get_attribute(attribute) or ""
                else:
                    v = (el.inner_text() or "").strip()
                if v:
                    values.append(v)

            if len(values) == 1:
                content_item[name] = values[0]
            elif len(values) > 1:
                content_item[name] = values
        except Exception:
            continue

    if content_item:
        data["content"].append(content_item)


def should_ignore(element, ignore_selectors: list) -> bool:
    """要素が無視セレクタに含まれるかチェックする"""
    for selector in ignore_selectors:
        try:
            parent = element.evaluate(
                "(el, sel) => el.closest(sel) !== null", selector
            )
            if parent:
                return True
        except Exception:
            continue
    return False


def extract_images(page, url: str, ignore: list, data: dict):
    """画像を抽出する（無視セレクタを考慮）"""
    ignore_css = ", ".join(ignore) if ignore else ""
    try:
        images = page.evaluate(f"""(params) => {{
            const baseUrl = params.baseUrl;
            const ignoreSelector = params.ignoreSelector;
            const imgs = [];
            const seen = new Set();
            document.querySelectorAll('img[src]').forEach(img => {{
                if (ignoreSelector && img.closest(ignoreSelector)) return;
                let src = img.src;
                if (!src || src.startsWith('data:')) return;
                try {{ src = new URL(src, baseUrl).href; }} catch {{}}
                if (seen.has(src)) return;
                seen.add(src);
                imgs.push({{src: src, alt: img.alt || ''}});
            }});
            return imgs;
        }}""", {"baseUrl": url, "ignoreSelector": ignore_css})
        data["images"] = images or []
    except Exception:
        pass


def extract_links(page, url: str, ignore: list, data: dict):
    """リンクを抽出する（無視セレクタを考慮）"""
    ignore_css = ", ".join(ignore) if ignore else ""
    try:
        links = page.evaluate(f"""(params) => {{
            const baseUrl = params.baseUrl;
            const ignoreSelector = params.ignoreSelector;
            const result = [];
            const seen = new Set();
            document.querySelectorAll('a[href]').forEach(a => {{
                if (ignoreSelector && a.closest(ignoreSelector)) return;
                let href = a.href;
                if (!href || href.startsWith('#') || href.startsWith('javascript:')) return;
                try {{ href = new URL(href, baseUrl).href; }} catch {{}}
                const text = a.innerText?.trim();
                if (!text) return;
                if (seen.has(href)) return;
                seen.add(href);
                result.push({{href: href, text: text}});
            }});
            return result;
        }}""", {"baseUrl": url, "ignoreSelector": ignore_css})
        data["links"] = links or []
    except Exception:
        pass


# ────────────────────────────────────────────
#  戦略ベースのページネーション
# ────────────────────────────────────────────

def find_next_page_with_strategy(page, current_url: str, strategy: dict) -> str | None:
    """戦略で指定されたセレクタを使ってページネーションリンクを検出する"""
    pagination = strategy.get("pagination", {})
    next_selector = pagination.get("next_selector")

    if not next_selector:
        return None

    parsed_current = urlparse(current_url)

    try:
        el = page.query_selector(next_selector)
        if el and el.is_visible():
            href = el.get_attribute("href")
            if href and not href.startswith("#") and not href.startswith("javascript:"):
                abs_url = urljoin(current_url, href)
                if urlparse(abs_url).netloc == parsed_current.netloc:
                    return abs_url
    except Exception as e:
        log(f"戦略ページネーション検出エラー: {e}")

    return None


# ────────────────────────────────────────────
#  「続きを表示」/「もっと見る」ボタンのクリック
# ────────────────────────────────────────────

LOAD_MORE_PATTERNS = [
    # 日本語
    r"もっと見る", r"続きを表示", r"続きを読む", r"さらに表示",
    r"全て表示", r"すべて表示", r"もっと読む", r"詳細を表示",
    r"レビューをもっと見る", r"もっと読み込む", r"追加で表示",
    # 英語
    r"show\s*more", r"load\s*more", r"see\s*more", r"view\s*more",
    r"read\s*more", r"show\s*all", r"view\s*all", r"see\s*all",
    r"load\s*more\s*comments", r"more\s*replies",
]

LOAD_MORE_SELECTORS = [
    'button[class*="more"]',
    'button[class*="load"]',
    'a[class*="more"]',
    'a[class*="load"]',
    'button[class*="expand"]',
    '[class*="show-more"]',
    '[class*="showMore"]',
    '[class*="load-more"]',
    '[class*="loadMore"]',
    '[data-action="load-more"]',
    '[data-click="loadMore"]',
]


def _try_click(el, page, wait_ms: int) -> bool:
    """
    要素のクリックを試みる。
    オーバーレイ等にポインターイベントを遮られた場合は force=True でリトライする。
    """
    try:
        el.scroll_into_view_if_needed()
        el.click(timeout=3000)
        page.wait_for_timeout(wait_ms)
        return True
    except Exception as click_err:
        err_msg = str(click_err)
        if "intercepts pointer events" in err_msg or "Timeout" in err_msg:
            # オーバーレイに遮られているため、まず除去を試みてからリトライ
            _dismiss_overlays(page)
            try:
                el.click(timeout=3000)
                page.wait_for_timeout(wait_ms)
                return True
            except Exception:
                # それでも失敗する場合は force クリック
                try:
                    el.click(force=True, timeout=3000)
                    page.wait_for_timeout(wait_ms)
                    return True
                except Exception as force_err:
                    log(f"force クリックも失敗: {force_err}")
                    return False
        raise


def _dismiss_overlays(page):
    """ページ上のオーバーレイ（モーダル、ログイン促進等）を除去する"""
    try:
        page.evaluate("""() => {
            // #layers 直下の子要素を除去（X/Twitter のオーバーレイ構造）
            const layers = document.querySelector('#layers');
            if (layers) {
                while (layers.firstChild) {
                    layers.removeChild(layers.firstChild);
                }
            }
            // 一般的なモーダル / オーバーレイを除去
            const selectors = [
                '[class*="overlay"]', '[class*="modal"]', '[class*="popup"]',
                '[class*="dialog"]', '[role="dialog"]',
            ];
            for (const sel of selectors) {
                document.querySelectorAll(sel).forEach(el => {
                    // body 直下やメインコンテンツを誤って消さないよう、
                    // position が fixed/absolute のもののみ対象
                    const style = window.getComputedStyle(el);
                    if (style.position === 'fixed' || style.position === 'absolute') {
                        el.remove();
                    }
                });
            }
        }""")
        log("オーバーレイを除去しました")
    except Exception as e:
        log(f"オーバーレイ除去中にエラー: {e}")


def click_load_more_buttons(page, max_clicks: int = 10, wait_ms: int = 2000):
    """
    ページ上の「もっと見る」系ボタンを繰り返しクリックして
    動的コンテンツを全て読み込む。
    """
    total_clicks = 0
    consecutive_no_change = 0

    for _ in range(max_clicks):
        current_height = page.evaluate("document.body.scrollHeight")
        clicked = False

        # 1. テキストパターンによるボタン検索
        for pattern in LOAD_MORE_PATTERNS:
            try:
                elements = page.query_selector_all("button, [role='button']")
                for el in elements:
                    try:
                        text = (el.inner_text() or "").strip()
                        if text and re.search(pattern, text, re.IGNORECASE):
                            if el.is_visible() and el.is_enabled():
                                if _try_click(el, page, wait_ms):
                                    total_clicks += 1
                                    clicked = True
                                    log(f"ボタンクリック: '{text}' (#{total_clicks})")
                                    break
                    except Exception as e:
                        log(f"ボタンクリック処理中にエラー: {e}")
                        continue
                if clicked:
                    break
            except Exception as e:
                log(f"ボタン検索中にエラー: {e}")
                continue

        # 2. CSSセレクタによるボタン検索
        if not clicked:
            for selector in LOAD_MORE_SELECTORS:
                try:
                    el = page.query_selector(selector)
                    if el and el.is_visible():
                        try:
                            if el.is_enabled():
                                if _try_click(el, page, wait_ms):
                                    total_clicks += 1
                                    clicked = True
                                    text = (el.inner_text() or "").strip()[:30]
                                    log(f"セレクタクリック: '{text}' ({selector}) (#{total_clicks})")
                                    break
                        except Exception as e:
                            log(f"セレクタクリック処理中にエラー: {e}")
                            continue
                except Exception as e:
                    log(f"セレクタ検索中にエラー ({selector}): {e}")
                    continue

        # 3. 無限スクロール
        if not clicked:
            page.evaluate("window.scrollTo(0, document.body.scrollHeight)")
            page.wait_for_timeout(wait_ms)

        new_height = page.evaluate("document.body.scrollHeight")
        if new_height == current_height and not clicked:
            consecutive_no_change += 1
            if consecutive_no_change >= 3:
                log(f"コンテンツ読み込み完了 (クリック数: {total_clicks})")
                break
        else:
            consecutive_no_change = 0

    return total_clicks


# ────────────────────────────────────────────
#  Cookie バナー
# ────────────────────────────────────────────

def dismiss_cookie_banners(page):
    """Cookie同意バナーを閉じる（プライバシー保護のため「拒否」を優先）"""
    dismiss_patterns = [
        r"拒否", r"すべて拒否", r"reject\s*all", r"decline",
        r"閉じる", r"close", r"dismiss",
        r"同意", r"承認", r"accept", r"agree", r"got\s*it", r"ok",
    ]
    try:
        for pattern in dismiss_patterns:
            elements = page.query_selector_all("button, a, [role='button']")
            for el in elements:
                try:
                    text = (el.inner_text() or "").strip()
                    if text and re.search(pattern, text, re.IGNORECASE) and el.is_visible():
                        el.click()
                        page.wait_for_timeout(500)
                        log(f"Cookie バナーを閉じました: '{text}'")
                        return
                except Exception as e:
                    log(f"Cookie バナー要素処理中にエラー: {e}")
                    continue
    except Exception as e:
        log(f"Cookie バナー処理中にエラー: {e}")


# ────────────────────────────────────────────
#  メイン
# ────────────────────────────────────────────

def main():
    if len(sys.argv) < 3:
        log("Usage: scrape_page.py <url> <output_path>")
        sys.exit(1)

    url = sys.argv[1]
    output_path = sys.argv[2]

    log(f"URL: {url}")
    log(f"出力先: {output_path}")

    # Claude Code CLI 設定を環境変数から取得
    node_path = os.environ.get("CLAUDE_NODE_PATH", "")
    cli_path = os.environ.get("CLAUDE_CLI_PATH", "")

    if not node_path or not cli_path:
        log("エラー: CLAUDE_NODE_PATH / CLAUDE_CLI_PATH 環境変数が設定されていません")
        sys.exit(1)

    log(f"Claude Code CLI: node={node_path} / cli={cli_path}")

    # Playwright の利用可能性チェック
    if sync_playwright is None:
        log("playwright がインストールされていません")
        sys.exit(2)

    # ブラウザがインストールされているかチェック
    if not check_playwright_browsers():
        install_playwright_browsers()

    # C#側タイムアウト(300秒)より余裕を持たせた制限（秒）
    SCRAPE_TIME_LIMIT = 240

    log("ブラウザを起動中...")
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        context = browser.new_context(
            user_agent="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            viewport={"width": 1920, "height": 1080},
            locale="ja-JP",
        )
        page = context.new_page()

        try:
            all_pages = []
            visited_urls = set()
            current_url = url
            page_num = 0
            max_pages = 100  # 安全上限
            strategy = None
            scrape_start = time.monotonic()

            def remaining_time() -> float:
                return SCRAPE_TIME_LIMIT - (time.monotonic() - scrape_start)

            while current_url and page_num < max_pages:
                if remaining_time() <= 30:
                    log(f"残り時間が少ないため処理を終了します（残り {remaining_time():.0f}秒）")
                    break

                # 正規化して重複チェック
                normalized = current_url.split("?")[0].split("#")[0].rstrip("/")
                if normalized in visited_urls:
                    log(f"既に訪問済みのURL、ページネーション終了: {current_url}")
                    break
                visited_urls.add(normalized)
                page_num += 1

                log(f"--- ページ {page_num} ---")
                log(f"アクセス中: {current_url}")
                try:
                    page.goto(current_url, wait_until="domcontentloaded", timeout=60000)
                except Exception as nav_error:
                    log(f"ナビゲーション警告: {nav_error}")
                    log("ページの読み込みを続行します...")
                log(f"読み込み完了: {page.title()}")

                # Cookie バナーを閉じる（初回のみ）
                if page_num == 1:
                    dismiss_cookie_banners(page)
                    _dismiss_overlays(page)

                # Claude Code CLI に戦略を問い合わせ（初回）
                if strategy is None:
                    log("ページ構造を分析中...")
                    page_summary = get_page_summary(page, current_url)
                    strategy = generate_scraping_strategy(page_summary)

                # 動的コンテンツの段階的読み込み + 抽出ループ
                round_num = 0
                prev_content_count = 0
                while remaining_time() > 30:
                    round_num += 1
                    log(f"ラウンド {round_num}（残り {remaining_time():.0f}秒）")

                    # 10回クリックして動的コンテンツを読み込む
                    clicks = click_load_more_buttons(page)
                    if clicks > 0:
                        log(f"動的コンテンツ読み込み (クリック数: {clicks})")

                    # 戦略ベースでデータ抽出
                    try:
                        page_data = extract_with_strategy(page, current_url, strategy)
                    except RuntimeError:
                        if round_num == 1:
                            raise
                        log("追加コンテンツの抽出に失敗、このページの処理を終了します")
                        break

                    current_content_count = len(page_data.get("content", []))
                    log(f"抽出コンテンツ数: {current_content_count}")

                    # 初回ラウンドまたはコンテンツが増えた場合のみ結果を更新
                    if round_num == 1:
                        page_data["page_number"] = page_num
                        all_pages.append(page_data)
                    elif current_content_count > prev_content_count:
                        page_data["page_number"] = page_num
                        all_pages[-1] = page_data  # 最新の抽出結果で上書き
                    else:
                        log("新しいコンテンツが見つからないため、このページの読み込みを完了します")
                        break

                    prev_content_count = current_content_count

                    # クリックが0回（ボタンもスクロールも効果なし）なら終了
                    if clicks == 0:
                        log("クリック可能な要素がないため、このページの読み込みを完了します")
                        break

                    # 次のラウンド前に Claude Code CLI に戦略を再問い合わせ
                    if remaining_time() > 60:
                        log("ページ構造を再分析中...")
                        page_summary = get_page_summary(page, current_url)
                        try:
                            strategy = generate_scraping_strategy(page_summary)
                        except Exception as strat_err:
                            log(f"戦略再生成に失敗、前回の戦略を継続します: {strat_err}")

                # ページネーション
                next_url = find_next_page_with_strategy(page, current_url, strategy)
                if next_url and next_url != current_url:
                    log(f"次のページを検出: {next_url}")
                    current_url = next_url
                else:
                    log("次のページが見つからないため、ページネーション終了")
                    current_url = None

            # 結果を構築
            elapsed = time.monotonic() - scrape_start
            log(f"スクレイピング完了（経過時間: {elapsed:.0f}秒）")

            if len(all_pages) == 1:
                result = all_pages[0]
                result["scraped_at"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
            else:
                result = {
                    "url": url,
                    "title": all_pages[0]["title"] if all_pages else "",
                    "total_pages": len(all_pages),
                    "pages": all_pages,
                    "scraped_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                }

            log(f"合計ページ数: {len(all_pages)}")
            total_content = sum(len(p.get("content", [])) for p in all_pages)
            log(f"合計コンテンツ数: {total_content}")

            # JSON出力
            with open(output_path, "w", encoding="utf-8") as f:
                json.dump(result, f, ensure_ascii=False, indent=2)
            log(f"JSONファイルを出力しました: {output_path}")

        except Exception as e:
            log(f"スクレイピングエラー: {e}")
            log(traceback.format_exc())
            sys.exit(1)
        finally:
            context.close()
            browser.close()
            log("ブラウザを終了しました")


if __name__ == "__main__":
    main()
