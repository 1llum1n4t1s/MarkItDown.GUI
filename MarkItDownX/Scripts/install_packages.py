"""
IronPython環境でpipを使ってライブラリをインストールするスクリプト
"""

import sys
import subprocess
import os

print("=== IronPython環境でpipインストールテスト ===")

# pipが利用できるかチェック
try:
    import pip
    print("✅ pipが利用可能です")
    print(f"pipバージョン: {pip.__version__}")
except ImportError:
    print("❌ pipが利用できません")
    sys.exit(1)

# インストールするパッケージリスト
packages = [
    "openpyxl",
    "python-docx", 
    "python-pptx",
    "Pillow",
    "pydub"
]

print(f"\n=== インストール対象パッケージ ===")
for package in packages:
    print(f"  - {package}")

# 各パッケージをインストール
for package in packages:
    print(f"\n=== {package}のインストール ===")
    try:
        # pip installコマンドを実行
        result = subprocess.run([
            sys.executable, "-m", "pip", "install", package
        ], capture_output=True, text=True)
        
        if result.returncode == 0:
            print(f"✅ {package}のインストールに成功")
        else:
            print(f"❌ {package}のインストールに失敗")
            print(f"エラー: {result.stderr}")
            
    except Exception as e:
        print(f"❌ {package}のインストール中にエラー: {e}")

print("\n=== インストール完了 ===") 