using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Providers;

namespace MarkItDown.GUI.Services;

// EnumとOptionsはそのままでOK
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public sealed class LoggerOptions
{
    public string AppName { get; set; } = "MarkItDown.GUI";
    public string? LogDirectoryOverride { get; set; }
    public int RollingSizeMb { get; set; } = 10;
}

public static class Logger
{
    private static ILoggerFactory? _loggerFactory;
    private static ILogger? _logger;
    private static string _appName = "MarkItDown.GUI";

    // 自前の MinLogLevel 判定は削除（ILoggerFactory側で制御するため）

    private static bool IsDebugRun
    {
        get
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory.AsSpan();
            return baseDir.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
                   baseDir.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string GetLogDirectory(string appName)
    {
        if (IsDebugRun)
        {
            return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appName);
    }

    public static void Initialize(LoggerOptions? options = null)
    {
        // 既に初期化済みなら何もしない（二重初期化防止）
        if (_loggerFactory != null) return;

        var opt = options ?? new LoggerOptions();
        _appName = opt.AppName;
        var logDirectory = opt.LogDirectoryOverride ?? GetLogDirectory(_appName);

        if (!Directory.Exists(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }

        _loggerFactory = LoggerFactory.Create(logging =>
        {
            // ここでログレベルを制御
#if DEBUG
            logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
#else
            logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
#endif

            // テキスト形式のフォーマットを一括設定
            // これにより、すべてのログに自動で [日付] [レベル] が付きます
            logging.AddZLoggerRollingFile(options =>
            {
                options.FilePathSelector = (timestamp, sequenceNumber) =>
                    Path.Combine(logDirectory, $"{_appName}_{timestamp.ToLocalTime():yyyyMMdd}_{sequenceNumber:000}.log");
                options.RollingSizeKB = opt.RollingSizeMb * 1024;
                
                // プレーンテキストフォーマッタを設定（ここでタイムスタンプ等の形式を決める）
                options.UsePlainTextFormatter(formatter =>
                {
                    formatter.SetPrefixFormatter($"{0:yyyy-MM-dd HH:mm:ss.fff} | {1} | ", (in template, in info) => template.Format(info.Timestamp, info.LogLevel));
                });
            });

            logging.AddZLoggerConsole(options =>
            {
                options.UsePlainTextFormatter(formatter =>
                {
                    formatter.SetPrefixFormatter($"{0:yyyy-MM-dd HH:mm:ss.fff} | {1} | ", (in template, in info) => template.Format(info.Timestamp, info.LogLevel));
                });
            });
        });

        _logger = _loggerFactory.CreateLogger(_appName);
        _logger.LogDebug("Logger initialized (RollingFile)");
    }

    public static void Log(string message, LogLevel level = LogLevel.Info)
    {
        // 初期化漏れガード（念のため）
        if (_logger == null) Initialize();

        // メッセージ生成済みの場合は標準のLogメソッドを使う（switch 文不要）
        var msLevel = ToMsLogLevel(level);
        _logger!.Log(msLevel, message);
    }

    public static void LogLines(string[] messages, LogLevel level = LogLevel.Info)
    {
        if (messages is null || messages.Length == 0) return;
        if (_logger == null) Initialize();

        var msLevel = ToMsLogLevel(level);
        foreach (var message in messages)
        {
            _logger!.Log(msLevel, message);
        }
    }

    public static void LogException(string message, Exception exception)
    {
        if (_logger == null) Initialize();

        // Errorレベルで例外付きログを出力
        _logger!.LogError(exception, message);
    }

    public static void LogStartup(string[] args)
    {
        if (_logger == null) Initialize();

        var argsLines = args.Length > 0
            ? string.Join(Environment.NewLine, args.Select((a, i) => $"  [{i}]: {a}"))
            : "  (none)";

        // ここは ZLogger の補間文字列構文を使ってもOK（インライン生成なので）
        // ただし、統一感を出すために標準Logメソッドでも可
        _logger!.LogDebug($"""
            === {_appName} 起動ログ ===
            実行ファイルパス: {Environment.ProcessPath}
            引数 ({args.Length}):
            {argsLines}
            """);
    }

    public static void Dispose()
    {
        _loggerFactory?.Dispose();
        _loggerFactory = null;
        _logger = null;
    }

    // 自前EnumをMS標準Enumに変換するヘルパー
    private static Microsoft.Extensions.Logging.LogLevel ToMsLogLevel(LogLevel level)
    {
        return level switch
        {
            LogLevel.Debug => Microsoft.Extensions.Logging.LogLevel.Debug,
            LogLevel.Info => Microsoft.Extensions.Logging.LogLevel.Information,
            LogLevel.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
            LogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
            _ => Microsoft.Extensions.Logging.LogLevel.Information
        };
    }
}