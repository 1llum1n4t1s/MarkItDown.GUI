# MarkItDownX

## 公開手順

1. `releases` ブランチにリリース用の変更をコミットします。
2. `releases` ブランチへ push すると、GitHub Actions の「Velopack リリース」ワークフローが実行されます。
3. ワークフローが Velopack でパッケージを作成し、GitHub Releases にアップロードします。

※ `main` ブランチへの push では公開は実行されません。
