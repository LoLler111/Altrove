using Altrove.Core.Protocol;

namespace Altrove.Core.Video;

/// <summary>
/// The resolutions a receiver can choose from (docs/PROTOCOLLO.md, <c>choices.resolutions</c>), and the size each
/// one gives. A choice is keyed by its resulting height, so asking for <c>maxHeight</c> = that height gives
/// exactly that size again. Receiver sizes and decoder limits are in landscape.
/// </summary>
public static class ResolutionPlanner
{
    private static readonly int[] StandardHeights = [2160, 1800, 1600, 1440, 1200, 1080, 1050, 900, 800, 768, 720, 600, 540];
    private const int SmallestHeight = 480;

    // ---- Duplica: the PC screen scaled down, never up ----

    /// <summary>Stream size for <paramref name="maxHeight"/>; 0 means the recommended one.</summary>
    public static (int Width, int Height) MirrorSize(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight,
        int displayWidth, int displayHeight, int chosenHeight)
    {
        if (chosenHeight <= 0)
            return MirrorRecommended(sourceWidth, sourceHeight, maxWidth, maxHeight, displayWidth, displayHeight);
        // A choice from the list gives exactly its size (one limited by width would not come back from its height alone).
        if (Mirror(sourceWidth, sourceHeight, maxWidth, maxHeight, displayWidth, displayHeight).FirstOrDefault(c => c.MaxHeight == chosenHeight) is { } listed)
            return (listed.Width, listed.Height);
        return StreamSizing.Fit(sourceWidth, sourceHeight, maxWidth, Math.Min(chosenHeight, maxHeight));
    }

    public static IReadOnlyList<ResolutionChoice> Mirror(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight,
        int displayWidth, int displayHeight)
    {
        var recommended = MirrorRecommended(sourceWidth, sourceHeight, maxWidth, maxHeight, displayWidth, displayHeight);
        var heights = StandardHeights.Append(sourceHeight).Append(recommended.Height);
        return Choices(heights.Select(h => StreamSizing.Fit(sourceWidth, sourceHeight, maxWidth, Math.Min(h, maxHeight))), recommended);
    }

    private static (int Width, int Height) MirrorRecommended(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight,
        int displayWidth, int displayHeight)
    {
        var (longSide, shortSide) = Landscape(displayWidth, displayHeight);
        return StreamSizing.Fit(sourceWidth, sourceHeight, Math.Min(maxWidth, longSide), Math.Min(maxHeight, shortSide));
    }

    // ---- Estendi: a virtual monitor shaped like the receiver, bigger than it too ----

    /// <summary>Virtual monitor size for <paramref name="chosenHeight"/>; 0 means the receiver's own screen.</summary>
    public static (int Width, int Height) ExtendSize(int displayWidth, int displayHeight, int maxWidth, int maxHeight, int chosenHeight)
    {
        var (longSide, shortSide) = Landscape(displayWidth, displayHeight);
        if (longSide <= 0 || shortSide <= 0)
            (longSide, shortSide) = (1920, 1080);
        var aspect = (double)longSide / shortSide;
        var height = Math.Min(chosenHeight <= 0 ? shortSide : chosenHeight, maxHeight);
        var width = (int)Math.Round(height * aspect);
        if (width > maxWidth)
        {
            width = maxWidth;
            height = (int)Math.Round(width / aspect);
        }
        return (width & ~1, height & ~1);
    }

    public static IReadOnlyList<ResolutionChoice> Extend(int displayWidth, int displayHeight, int maxWidth, int maxHeight)
    {
        var recommended = ExtendSize(displayWidth, displayHeight, maxWidth, maxHeight, 0);
        var heights = StandardHeights.Append(recommended.Height);
        return Choices(heights.Select(h => ExtendSize(displayWidth, displayHeight, maxWidth, maxHeight, h)), recommended);
    }

    // ---- shared ----

    private static IReadOnlyList<ResolutionChoice> Choices(IEnumerable<(int Width, int Height)> sizes, (int Width, int Height) recommended) =>
        sizes
            .Where(s => s.Height >= SmallestHeight || s == recommended)
            .Distinct()
            // One per height, the key the receiver asks with: the recommended one, else the widest.
            .GroupBy(s => s.Height)
            .Select(g => g.Contains(recommended) ? recommended : g.MaxBy(s => s.Width))
            .OrderByDescending(s => s.Width * s.Height)
            .Select(s => new ResolutionChoice(s.Height, s.Width, s.Height, s == recommended))
            .ToList();

    private static (int Long, int Short) Landscape(int width, int height) => (Math.Max(width, height), Math.Min(width, height));
}
