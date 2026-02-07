using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MarkItDown.GUI.Models;

/// <summary>
/// Reddit スレッド全体のデータモデル（投稿 + コメント）
/// </summary>
public class RedditThreadData
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("subreddit")]
    public string Subreddit { get; set; } = "";

    [JsonPropertyName("post")]
    public RedditPost Post { get; set; } = new();

    [JsonPropertyName("comments")]
    public List<RedditComment> Comments { get; set; } = [];

    [JsonPropertyName("scraped_at")]
    public string ScrapedAt { get; set; } = "";
}

/// <summary>
/// Reddit 投稿のデータモデル
/// </summary>
public class RedditPost
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("subreddit")]
    public string Subreddit { get; set; } = "";

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("upvote_ratio")]
    public double? UpvoteRatio { get; set; }

    [JsonPropertyName("created_utc")]
    public double? CreatedUtc { get; set; }

    [JsonPropertyName("selftext")]
    public string SelfText { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("permalink")]
    public string Permalink { get; set; } = "";

    [JsonPropertyName("num_comments")]
    public int NumComments { get; set; }

    [JsonPropertyName("is_video")]
    public bool? IsVideo { get; set; }

    [JsonPropertyName("link_flair_text")]
    public string? LinkFlairText { get; set; }

    [JsonPropertyName("domain")]
    public string? Domain { get; set; }
}

/// <summary>
/// Reddit コメントのデータモデル（再帰的な返信を含む）
/// </summary>
public class RedditComment
{
    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("created_utc")]
    public double? CreatedUtc { get; set; }

    [JsonPropertyName("is_submitter")]
    public bool? IsSubmitter { get; set; }

    [JsonPropertyName("edited")]
    public object? Edited { get; set; }

    [JsonPropertyName("distinguished")]
    public string? Distinguished { get; set; }

    [JsonPropertyName("replies")]
    public List<RedditComment> Replies { get; set; } = [];
}
