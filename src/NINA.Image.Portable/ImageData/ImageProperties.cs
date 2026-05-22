using NINA.Core.Enum;

namespace NINA.Image.ImageData;

public record ImageProperties {
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitDepth { get; init; }
    public bool IsBayered { get; init; }
    public BayerPatternEnum BayerPattern { get; init; } = BayerPatternEnum.None;

    /// <summary>
    /// Number of colour planes in the pixel buffer. 1 = grayscale (the
    /// default, matches every existing call site that didn't set this
    /// explicitly); 3 = RGB stored plane-sequentially (R plane first,
    /// then G, then B). RGB FITS files (NAXIS=3 with NAXIS3=3 — the
    /// PixInsight/Siril export convention) populate this so the
    /// thumbnailer can render in colour instead of dropping to the
    /// red channel.
    /// </summary>
    public int Channels { get; init; } = 1;

    public bool IsColor => Channels >= 3;

    public long PixelCount => (long)Width * Height;
}
