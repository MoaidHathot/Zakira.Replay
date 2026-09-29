using System.Globalization;

namespace Zakira.Replay.Core;

/// <summary>Projects the yt-dlp probe onto <see cref="SourceDetails"/> for the manifest.</summary>
public static class SourceDetailsBuilder
{
    /// <summary>Upper bound on comments embedded in the manifest; the full list lives in comments/comments.json.</summary>
    public const int MaxManifestComments = 20;

    /// <summary>Per-comment text cap inside the manifest (a pinned recipe fits; a wall of text does not).</summary>
    public const int MaxManifestCommentChars = 2000;

    public static SourceDetails? FromInfo(YtDlpInfo info, string? commentsPath)
    {
        var chapters = info.Chapters?
            .Where(chapter => chapter.StartTime is not null && !string.IsNullOrWhiteSpace(chapter.Title))
            .OrderBy(chapter => chapter.StartTime)
            .Select(chapter => new SourceChapter(
                chapter.Title!.Trim(),
                chapter.StartTime!.Value,
                chapter.EndTime,
                Timestamp.Format(chapter.StartTime.Value)))
            .ToList();

        var comments = SelectManifestComments(info.Comments);

        var details = new SourceDetails(
            Description: string.IsNullOrWhiteSpace(info.Description) ? null : info.Description.Trim(),
            Uploader: Clean(info.Uploader),
            Channel: Clean(info.Channel),
            UploadDate: FormatUploadDate(info.UploadDate),
            ViewCount: info.ViewCount,
            LikeCount: info.LikeCount,
            CommentCount: info.CommentCount,
            Tags: info.Tags is { Count: > 0 } ? info.Tags : null,
            Categories: info.Categories is { Count: > 0 } ? info.Categories : null,
            Chapters: chapters is { Count: > 0 } ? chapters : null,
            Comments: comments is { Count: > 0 } ? comments : null,
            CommentsPath: commentsPath);

        var isEmpty = details.Description is null && details.Uploader is null && details.Channel is null && details.UploadDate is null
            && details.ViewCount is null && details.LikeCount is null && details.CommentCount is null
            && details.Tags is null && details.Categories is null && details.Chapters is null && details.Comments is null;
        return isEmpty ? null : details;
    }

    /// <summary>
    /// Pinned and uploader-authored comments first (the ones that carry the recipe / links),
    /// then the rest by like count; top-level comments only; capped.
    /// </summary>
    public static List<SourceComment>? SelectManifestComments(IReadOnlyList<YtDlpComment>? comments)
    {
        if (comments is null || comments.Count == 0)
        {
            return null;
        }

        return comments
            .Where(comment => !string.IsNullOrWhiteSpace(comment.Text))
            .Where(comment => string.IsNullOrEmpty(comment.Parent) || comment.Parent == "root")
            .OrderByDescending(comment => comment.IsPinned == true)
            .ThenByDescending(comment => comment.AuthorIsUploader == true)
            .ThenByDescending(comment => comment.LikeCount ?? 0)
            .Take(MaxManifestComments)
            .Select(comment => new SourceComment(
                Author: Clean(comment.Author),
                Text: Truncate(comment.Text!.Trim(), MaxManifestCommentChars),
                LikeCount: comment.LikeCount,
                IsPinned: comment.IsPinned == true,
                AuthorIsUploader: comment.AuthorIsUploader == true,
                PublishedAt: comment.Timestamp is { } unix && unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : null,
                Id: comment.Id))
            .ToList();
    }

    /// <summary>yt-dlp's <c>YYYYMMDD</c> to ISO <c>YYYY-MM-DD</c>; anything else is passed through trimmed.</summary>
    public static string? FormatUploadDate(string? uploadDate)
    {
        if (string.IsNullOrWhiteSpace(uploadDate))
        {
            return null;
        }

        var trimmed = uploadDate.Trim();
        return trimmed.Length == 8 && trimmed.All(char.IsDigit)
            ? $"{trimmed[..4]}-{trimmed[4..6]}-{trimmed[6..]}"
            : trimmed;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd() + "…";
}
