using System;

namespace MarkItDown.GUI.Services;

/// <summary>
/// 変換・スクレイピング・依存準備など、再起動で壊れる処理の実行状態を共有する。
/// </summary>
public static class AppActivityTracker
{
    private static readonly object Gate = new();
    private static int _busyCount;
    private static bool _restartReserved;

    /// <summary>
    /// 再起動を避けたい処理が実行中かどうか。
    /// </summary>
    public static bool IsBusy
    {
        get
        {
            lock (Gate)
            {
                return _busyCount > 0;
            }
        }
    }

    /// <summary>
    /// 更新適用のための再起動が予約済みかどうか。
    /// </summary>
    public static bool IsRestartReserved
    {
        get
        {
            lock (Gate)
            {
                return _restartReserved;
            }
        }
    }

    /// <summary>
    /// 処理中スコープを開始する。更新再起動が予約済みなら例外にする。
    /// </summary>
    public static IDisposable BeginBusyScope()
    {
        if (TryBeginBusyScope(out var scope))
        {
            return scope;
        }

        throw new InvalidOperationException("更新適用の再起動が予約済みです。");
    }

    /// <summary>
    /// 処理中スコープを開始する。更新再起動が予約済みなら false を返す。
    /// </summary>
    public static bool TryBeginBusyScope(out IDisposable scope)
    {
        lock (Gate)
        {
            if (_restartReserved)
            {
                scope = NullScope.Instance;
                return false;
            }

            _busyCount++;
            scope = new BusyScope();
            return true;
        }
    }

    /// <summary>
    /// 処理が完全にアイドルなら、更新適用の再起動を予約する。
    /// </summary>
    public static bool TryReserveRestart()
    {
        lock (Gate)
        {
            if (_busyCount > 0 || _restartReserved)
            {
                return false;
            }

            _restartReserved = true;
            return true;
        }
    }

    /// <summary>
    /// 更新適用に失敗した場合に再起動予約を取り消す。
    /// </summary>
    public static void CancelReservedRestart()
    {
        lock (Gate)
        {
            _restartReserved = false;
        }
    }

    private static void EndBusyScope()
    {
        lock (Gate)
        {
            if (_busyCount > 0)
            {
                _busyCount--;
            }
        }
    }

    private sealed class BusyScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            EndBusyScope();
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
