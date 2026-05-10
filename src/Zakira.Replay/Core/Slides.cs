using System.Globalization;

namespace Zakira.Replay.Core;

/// <summary>
/// Groups frames whose perceptual hashes are within a Hamming distance threshold into slides.
/// Slides are facts about visible-content continuity: orchestrators can answer "when was slide X
/// visible?" and "which transcript segments spoke over slide X?" without redoing perceptual work.
/// </summary>
public static class SlideGrouper
{
    /// <summary>
    /// Groups consecutive (by timestamp) frames whose perceptual hashes are within
    /// <see cref="SlideGroupingOptions.HashDistance"/> Hamming bits of the current group's primary
    /// frame. Frames missing a hash always start a new slide. When grouping is disabled, every
    /// frame becomes its own slide so the contract stays uniform.
    /// </summary>
    public static IReadOnlyList<SlideArtifact> Group(IReadOnlyList<FrameArtifact> frames, SlideGroupingOptions options)
    {
        if (frames.Count == 0)
        {
            return [];
        }

        var ordered = frames
            .OrderBy(frame => frame.TimestampSeconds)
            .ThenBy(frame => frame.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var slides = new List<SlideArtifact>();
        var groupNumber = 0;

        if (!options.Enabled)
        {
            foreach (var frame in ordered)
            {
                groupNumber++;
                slides.Add(BuildSlide(groupNumber, [frame]));
            }

            return slides;
        }

        var threshold = Math.Max(0, options.HashDistance);
        var current = new List<FrameArtifact>();
        ulong? primaryHash = null;
        foreach (var frame in ordered)
        {
            var hash = TryParseHash(frame.PerceptualHash);
            if (current.Count == 0 || hash is null || primaryHash is null || HammingDistance(primaryHash.Value, hash.Value) > threshold)
            {
                if (current.Count > 0)
                {
                    groupNumber++;
                    slides.Add(BuildSlide(groupNumber, current));
                }

                current = [frame];
                primaryHash = hash;
            }
            else
            {
                current.Add(frame);
            }
        }

        if (current.Count > 0)
        {
            groupNumber++;
            slides.Add(BuildSlide(groupNumber, current));
        }

        return slides;
    }

    private static SlideArtifact BuildSlide(int groupNumber, IReadOnlyList<FrameArtifact> frames)
    {
        var primary = frames[0];
        var first = frames[0];
        var last = frames[^1];
        return new SlideArtifact(
            Id: $"slide-{groupNumber:000}",
            FirstSeenSeconds: first.TimestampSeconds,
            LastSeenSeconds: last.TimestampSeconds,
            FirstSeenLabel: first.TimestampLabel,
            LastSeenLabel: last.TimestampLabel,
            PrimaryFrameId: primary.Id,
            FrameIds: frames.Select(frame => frame.Id).ToArray(),
            PerceptualHash: primary.PerceptualHash);
    }

    internal static int HammingDistance(ulong left, ulong right)
    {
        return System.Numerics.BitOperations.PopCount(left ^ right);
    }

    internal static ulong? TryParseHash(string? hexHash)
    {
        if (string.IsNullOrWhiteSpace(hexHash))
        {
            return null;
        }

        return ulong.TryParse(hexHash, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}

/// <summary>
/// Tunables for <see cref="SlideGrouper.Group"/>.
/// </summary>
/// <param name="Enabled">When false, every frame becomes its own slide. Useful for animated UI walkthroughs where every frame matters.</param>
/// <param name="HashDistance">Maximum Hamming distance (0-64) between two 64-bit dHash values still considered the same slide. Default 6.</param>
public sealed record SlideGroupingOptions(bool Enabled = true, int HashDistance = 6);
