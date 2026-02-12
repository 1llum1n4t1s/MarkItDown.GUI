"""
HTTP + Claude Code CLI を使用した AI ガイド型 Web スクレイピングスクリプト（ブラウザ不要版）。

Playwright の代わりに requests + BeautifulSoup を使用し、
Claude Code CLI にページ構造を分析させて最適な CSS セレクタと抽出戦略を動的に決定する。

Usage:
    python scrape_page_http.py <url> <output_path>

環境変数:
    CLAUDE_NODE_PATH - Node.js の実行パス
    CLAUDE_CLI_PATH  - Claude Code CLI の実行パス

出力: JSON形式のページデータ（scrape_page.py と互換）
"""

import json
import os
import re
import subprocess
import sys
import time
import traceback
from collections import Counter
from urllib.parse import urljoin, urlparse

import requests
from bs4 import BeautifulSoup, Tag


def log(msg: str):
    print(msg, flush=True)


# ────────────────────────────────────────────
#  HTTP でページ取得
# ────────────────────────────────────────────

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/131.0.0.0 Safari/537.36"
    ),
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
    "Accept-Language": "ja,en-US;q=0.9,en;q=0.8",
    "Accept-Encoding": "gzip, deflate, br",
}


def fetch_html(url: str, timeout: int = 60) -> tuple[str, str]:
    """URLからHTMLを取得し、(html, final_url) を返す。"""
    resp = requests.get(url, headers=DEFAULT_HEADERS, timeout=timeout, allow_redirects=True)
    resp.raise_for_status()
    resp.encoding = resp.apparent_encoding or "utf-8"
    return resp.text, resp.url


# ────────────────────────────────────────────
#  Claude Code CLI 呼び出し
# ────────────────────────────────────────────

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


# ────────────────────────────────────────────
#  BeautifulSoup でページ分析（Claude CLI 送信用）
# ────────────────────────────────────────────

def get_page_summary(soup: BeautifulSoup, url: str) -> dict:
    """
    ページ構造のサマリーを生成する（Claude CLI に送信するためのコンパクトな情報）。
    scrape_page.py の get_page_summary と同等の情報を生成。
    """
    title = soup.title.string.strip() if soup.title and soup.title.string else ""

    summary = {
        "url": url,
        "title": title,
        "sample_html": "",
        "dom_stats": {},
        "visible_text_sample": "",
    }

    # DOM統計
    try:
        all_tags = soup.find_all(True)
        tag_counts = Counter(tag.name for tag in all_tags)

        # class名の出現頻度 Top 20
        class_counts: Counter[str] = Counter()
        for tag in all_tags:
            classes = tag.get("class", [])
            if isinstance(classes, list):
                for cls in classes:
                    if 1 < len(cls) < 50:
                        class_counts[cls] += 1
        top_classes = [f"{cls}({count})" for cls, count in class_counts.most_common(20)]

        # id一覧 Top 20
        ids = []
        for tag in all_tags:
            tag_id = tag.get("id", "")
            if tag_id and len(tag_id) < 50:
                ids.append(tag_id)
                if len(ids) >= 20:
                    break

        summary["dom_stats"] = {
            "total_elements": len(all_tags),
            "tag_counts": dict(tag_counts),
            "common_classes": top_classes,
            "ids": ids,
        }
    except Exception as e:
        log(f"DOM統計取得エラー: {e}")

    # HTMLサンプル（script/style除去、先頭4000文字）
    try:
        clone = BeautifulSoup(str(soup), "html.parser")
        for tag in clone.find_all(["script", "style", "noscript", "svg", "iframe"]):
            tag.decompose()
        html_str = str(clone)
        summary["sample_html"] = html_str[:4000]
    except Exception as e:
        log(f"HTMLサンプル取得エラー: {e}")

    # 可視テキストのサンプル（先頭500文字）
    try:
        body = soup.body
        if body:
            text = body.get_text(separator="\n", strip=True)
            summary["visible_text_sample"] = text[:500]
    except Exception:
        pass

    return summary


# ────────────────────────────────────────────
#  Claude CLI 戦略生成（scrape_page.py と共通ロジック）
# ────────────────────────────────────────────

def build_strategy_prompt(page_summary: dict) -> str:
    """Claude CLI に送るスクレイピング戦略生成プロンプトを構築する"""
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
    first_brace = text.find("{")
    first_bracket = text.find("[")

    if first_brace == -1 and first_bracket == -1:
        return None

    if first_brace != -1 and (first_bracket == -1 or first_brace < first_bracket):
        start_pos = first_brace
        open_char = "{"
        close_char = "}"
    else:
        start_pos = first_bracket
        open_char = "["
        close_char = "]"

    depth = 0
    in_string = False
    escape_next = False
    end_pos = -1

    for i in range(start_pos, len(text)):
        c = text[i]
        if escape_next:
            escape_next = False
            continue
        if c == "\\" and in_string:
            escape_next = True
            continue
        if c == '"':
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
        candidate = text[start_pos : end_pos + 1]
        try:
            json.loads(candidate)
            return candidate
        except json.JSONDecodeError:
            pass

    return None


def generate_scraping_strategy(page_summary: dict) -> dict:
    """Claude Code CLI にページ構造を分析させ、スクレイピング戦略 JSON を返す。"""
    prompt = build_strategy_prompt(page_summary)

    system_instruction = (
        "あなたはWeb スクレイピングの専門家です。"
        "与えられた Web ページの構造情報を分析し、最適なスクレイピング戦略を JSON で返してください。"
        "出力は JSON のみにしてください。説明文やマークダウンは不要です。"
    )

    full_prompt = f"{system_instruction}\n\n{prompt}"

    log("Claude Code CLI にスクレイピング戦略を問い合わせ中...")
    response_text = claude_call(full_prompt)
    if not response_text:
        raise ValueError("Claude Code CLI から応答を取得できませんでした")

    log(f"Claude CLI 応答長: {len(response_text)} 文字")

    strategy_json = extract_json_from_text(response_text)
    if not strategy_json:
        log(f"Claude CLI の応答から有効な JSON を抽出できませんでした: {response_text[:200]}")
        raise ValueError("Claude Code CLI から有効なスクレイピング戦略を取得できませんでした")

    strategy = json.loads(strategy_json)
    log(f"戦略生成完了: page_type={strategy.get('page_type', 'unknown')}")
    return strategy


# ────────────────────────────────────────────
#  戦略ベースのデータ抽出（BeautifulSoup版）
# ────────────────────────────────────────────

def should_ignore(element: Tag, ignore_selectors: list) -> bool:
    """要素が無視セレクタに含まれるかチェックする"""
    for selector in ignore_selectors:
        try:
            # 要素自身またはその祖先が無視セレクタにマッチするかチェック
            for parent in [element] + list(element.parents):
                if isinstance(parent, Tag):
                    try:
                        if parent.select_one(selector) is parent or parent.name == selector:
                            return True
                    except Exception:
                        pass
                    # セレクタに直接マッチするかチェック
                    try:
                        matches = parent.parent.select(selector) if parent.parent else []
                        if parent in matches:
                            return True
                    except Exception:
                        pass
        except Exception:
            continue
    return False


def extract_metadata(soup: BeautifulSoup, data: dict):
    """メタタグを抽出する"""
    try:
        for m in soup.find_all("meta"):
            name = m.get("name") or m.get("property") or ""
            content = m.get("content", "")
            if name and content:
                data["metadata"][name] = content
    except Exception:
        pass


def extract_json_ld(soup: BeautifulSoup, data: dict):
    """JSON-LD 構造化データを抽出する"""
    try:
        ld_elements = soup.find_all("script", {"type": "application/ld+json"})
        ld_list = []
        for ld_el in ld_elements:
            try:
                ld_text = ld_el.string
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


def _get_element_value(el: Tag, attribute: str | None) -> str:
    """要素から値を取得する"""
    if attribute:
        return el.get(attribute, "") or ""
    return el.get_text(strip=True)


def extract_list_items(soup: BeautifulSoup, items_selector: str, fields: list, ignore: list, data: dict):
    """リストページから各アイテムを抽出する"""
    try:
        items = soup.select(items_selector)
        log(f"アイテム数: {len(items)} (セレクタ: {items_selector})")

        for item in items:
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
                    el = item.select_one(selector)
                    if el:
                        value = _get_element_value(el, attribute)
                        if value:
                            item_data[name] = value
                except Exception:
                    continue

            # フィールドが取得できなかった場合、アイテム全体のテキストを取得
            if not item_data:
                try:
                    text = item.get_text(strip=True)
                    if text and len(text) >= 3:
                        item_data["text"] = text
                except Exception:
                    continue

            if item_data:
                data["content"].append(item_data)

    except Exception as e:
        log(f"リストアイテム抽出エラー: {e}")


def extract_from_container(soup: BeautifulSoup, container_selector: str, fields: list, ignore: list, data: dict):
    """メインコンテナからフィールドを抽出する"""
    try:
        container = soup.select_one(container_selector)
        if not container:
            log(f"メインコンテナが見つかりません: {container_selector}")
            extract_fields_global(soup, fields, ignore, data)
            return

        content_item = {}
        for field in fields:
            name = field.get("name", "")
            selector = field.get("selector", "")
            attribute = field.get("attribute")

            if not name or not selector:
                continue

            try:
                el = container.select_one(selector)
                if el and not should_ignore(el, ignore):
                    value = _get_element_value(el, attribute)
                    if value:
                        content_item[name] = value
            except Exception:
                continue

        if content_item:
            data["content"].append(content_item)
        else:
            # フィールド抽出に失敗した場合、コンテナ全体のテキストを取得
            try:
                text = container.get_text(strip=True)
                if text:
                    data["content"].append({"text": text})
            except Exception:
                pass

    except Exception as e:
        log(f"コンテナ抽出エラー: {e}")


def extract_fields_global(soup: BeautifulSoup, fields: list, ignore: list, data: dict):
    """ページ全体からフィールドを抽出する"""
    content_item = {}
    for field in fields:
        name = field.get("name", "")
        selector = field.get("selector", "")
        attribute = field.get("attribute")

        if not name or not selector:
            continue

        try:
            elements = soup.select(selector)
            values = []
            for el in elements:
                if should_ignore(el, ignore):
                    continue
                v = _get_element_value(el, attribute)
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


def extract_images(soup: BeautifulSoup, url: str, ignore: list, data: dict):
    """画像を抽出する"""
    try:
        imgs = []
        seen = set()
        for img in soup.find_all("img", src=True):
            if should_ignore(img, ignore):
                continue
            src = img.get("src", "")
            if not src or src.startswith("data:"):
                continue
            src = urljoin(url, src)
            if src in seen:
                continue
            seen.add(src)
            imgs.append({"src": src, "alt": img.get("alt", "")})
        data["images"] = imgs
    except Exception:
        pass


def extract_links(soup: BeautifulSoup, url: str, ignore: list, data: dict):
    """リンクを抽出する"""
    try:
        links = []
        seen = set()
        for a in soup.find_all("a", href=True):
            if should_ignore(a, ignore):
                continue
            href = a.get("href", "")
            if not href or href.startswith("#") or href.startswith("javascript:"):
                continue
            href = urljoin(url, href)
            text = a.get_text(strip=True)
            if not text:
                continue
            if href in seen:
                continue
            seen.add(href)
            links.append({"href": href, "text": text})
        data["links"] = links
    except Exception:
        pass


def extract_with_strategy(soup: BeautifulSoup, url: str, strategy: dict) -> dict:
    """Claude Code CLI が生成した戦略に基づいてページデータを抽出する。"""
    title = soup.title.string.strip() if soup.title and soup.title.string else ""

    data = {
        "url": url,
        "title": title,
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
    extract_metadata(soup, data)

    # JSON-LD
    extract_json_ld(soup, data)

    # 戦略ベースのコンテンツ抽出
    items_selector = content_selectors.get("items")
    main_container = content_selectors.get("main_container")

    if items_selector:
        extract_list_items(soup, items_selector, extraction_fields, ignore_selectors, data)
    elif main_container:
        extract_from_container(soup, main_container, extraction_fields, ignore_selectors, data)
    else:
        extract_fields_global(soup, extraction_fields, ignore_selectors, data)

    # 画像
    extract_images(soup, url, ignore_selectors, data)

    # リンク
    extract_links(soup, url, ignore_selectors, data)

    content_count = len(data.get("content", []))
    if content_count == 0:
        raise RuntimeError(
            f"戦略ベースの抽出でコンテンツが見つかりませんでした。"
            f"戦略: {json.dumps(strategy, ensure_ascii=False)[:300]}"
        )

    return data


# ────────────────────────────────────────────
#  ページネーション
# ────────────────────────────────────────────

def find_next_page_with_strategy(soup: BeautifulSoup, current_url: str, strategy: dict) -> str | None:
    """戦略で指定されたセレクタを使ってページネーションリンクを検出する"""
    pagination = strategy.get("pagination", {})
    next_selector = pagination.get("next_selector")

    if not next_selector:
        return None

    parsed_current = urlparse(current_url)

    try:
        el = soup.select_one(next_selector)
        if el:
            href = el.get("href", "")
            if href and not href.startswith("#") and not href.startswith("javascript:"):
                abs_url = urljoin(current_url, href)
                if urlparse(abs_url).netloc == parsed_current.netloc:
                    return abs_url
    except Exception as e:
        log(f"戦略ページネーション検出エラー: {e}")

    return None


# ────────────────────────────────────────────
#  メイン
# ────────────────────────────────────────────

def main():
    if len(sys.argv) < 3:
        log("Usage: scrape_page_http.py <url> <output_path>")
        sys.exit(1)

    url = sys.argv[1]
    output_path = sys.argv[2]

    log(f"URL: {url}")
    log(f"出力先: {output_path}")

    # Claude Code CLI 設定を環境変数から取得
    claude_node_path = os.environ.get("CLAUDE_NODE_PATH", "")
    claude_cli_path = os.environ.get("CLAUDE_CLI_PATH", "")

    if not claude_node_path or not claude_cli_path:
        log("エラー: CLAUDE_NODE_PATH / CLAUDE_CLI_PATH 環境変数が設定されていません")
        sys.exit(1)

    log(f"Claude CLI: node={claude_node_path} / cli={claude_cli_path}")

    # C#側タイムアウト(300秒)より余裕を持たせた制限（秒）
    SCRAPE_TIME_LIMIT = 240

    try:
        all_pages = []
        visited_urls = set()
        current_url = url
        page_num = 0
        max_pages = 100
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
            log(f"HTTP でアクセス中: {current_url}")

            try:
                html, final_url = fetch_html(current_url)
            except Exception as e:
                log(f"HTTP 取得エラー: {e}")
                if page_num == 1:
                    raise
                log("このページをスキップします")
                break

            log(f"HTML 取得完了 ({len(html)} bytes)")
            soup = BeautifulSoup(html, "html.parser")
            log(f"ページタイトル: {soup.title.string.strip() if soup.title and soup.title.string else '(なし)'}")

            # Claude Code CLI に戦略を問い合わせ（初回）
            if strategy is None:
                log("ページ構造を分析中...")
                page_summary = get_page_summary(soup, final_url)
                strategy = generate_scraping_strategy(page_summary)

            # 戦略ベースでデータ抽出
            try:
                page_data = extract_with_strategy(soup, final_url, strategy)
            except RuntimeError as e:
                if page_num == 1:
                    raise
                log(f"抽出エラー: {e}")
                break

            content_count = len(page_data.get("content", []))
            log(f"抽出コンテンツ数: {content_count}")

            page_data["page_number"] = page_num
            all_pages.append(page_data)

            # ページネーション
            next_url = find_next_page_with_strategy(soup, final_url, strategy)
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


if __name__ == "__main__":
    main()
