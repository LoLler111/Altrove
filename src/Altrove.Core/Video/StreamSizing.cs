namespace Altrove.Core.Video;

public static class StreamSizing
{
    /// <summary>
    /// The size of the encoded stream: the captured screen scaled down (never up) to fit inside the
    /// receiver's limits, keeping the aspect ratio, with even sides as H.264 4:2:0 requires.
    /// </summary>
    public static (int Width, int Height) Fit(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "The source has no size.");
        // A portrait-limited receiver (a phone held upright) still decodes the landscape size.
        if (maxWidth > 0 && maxHeight > 0 && (sourceWidth >= sourceHeight) != (maxWidth >= maxHeight))
            (maxWidth, maxHeight) = (maxHeight, maxWidth);

        var scale = 1.0;
        if (maxWidth > 0)
            scale = Math.Min(scale, (double)maxWidth / sourceWidth);
        if (maxHeight > 0)
            scale = Math.Min(scale, (double)maxHeight / sourceHeight);

        var width = Even((int)Math.Floor(sourceWidth * scale + 1e-6));
        var height = Even((int)Math.Floor(sourceHeight * scale + 1e-6));
        return (Math.Max(width, 2), Math.Max(height, 2));
    }

    private static int Even(int value) => value & ~1;
}
