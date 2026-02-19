using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarkItDown.GUI.Models;

/// <summary>
/// AOT対応のSystem.Text.Json Source Generator コンテキスト
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(RedditThreadData))]
[JsonSerializable(typeof(RedditSubredditData))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(List<JsonElement>))]
public partial class AppJsonContext : JsonSerializerContext;

/// <summary>
/// Reddit JSON出力用（WriteIndented + UnsafeRelaxedJsonEscaping + WhenWritingNull無視）
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RedditThreadData))]
[JsonSerializable(typeof(RedditSubredditData))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(List<JsonElement>))]
public partial class AppJsonIndentedContext : JsonSerializerContext;
