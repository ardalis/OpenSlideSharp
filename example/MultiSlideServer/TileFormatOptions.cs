namespace MultiSlideServer;

/// <summary>
/// Configuration options for tile image format.
/// Supports lossless formats (PNG) for regulatory compliance at high resolution levels.
/// </summary>
public class TileFormatOptions
{
    /// <summary>
    /// Default format for tiles: "jpeg" or "png"
    /// </summary>
    public string Default { get; set; } = "jpeg";

    /// <summary>
    /// Format for high-resolution tiles: "png" for lossless, or null to use Default.
    /// PNG is guaranteed lossless, while JPEG is always lossy even at quality 100.
    /// </summary>
    public string? LosslessFormat { get; set; } = "png";

    /// <summary>
    /// Number of highest-resolution levels to serve as lossless.
    /// 0 = none (all use Default format)
    /// 1 = only highest resolution level uses LosslessFormat
    /// N = top N levels use LosslessFormat
    /// -1 = all levels use LosslessFormat
    /// </summary>
    public int LosslessLevelCount { get; set; } = 1;

    /// <summary>
    /// JPEG quality (1-100) when using JPEG format. Higher = better quality but larger files.
    /// </summary>
    public int JpegQuality { get; set; } = 90;

    /// <summary>
    /// Determines the effective format for a given zoom level.
    /// </summary>
    /// <param name="level">The current zoom level (0-based)</param>
    /// <param name="totalLevels">Total number of zoom levels</param>
    /// <returns>The format to use: "jpeg" or "png"</returns>
    public string GetFormatForLevel(int level, int totalLevels)
    {
        if (string.IsNullOrEmpty(LosslessFormat) || LosslessLevelCount == 0)
        {
            return NormalizeFormat(Default);
        }

        bool useLossless = LosslessLevelCount switch
        {
            -1 => true, // All levels use lossless
            _ => level >= (totalLevels - LosslessLevelCount) // Top N levels
        };

        return useLossless ? NormalizeFormat(LosslessFormat) : NormalizeFormat(Default);
    }

    /// <summary>
    /// Gets the content type for a given format.
    /// </summary>
    public static string GetContentType(string format) => format switch
    {
        "png" => "image/png",
        _ => "image/jpeg"
    };

    /// <summary>
    /// Gets the file extension for a given format.
    /// </summary>
    public static string GetFileExtension(string format) => format switch
    {
        "png" => "png",
        _ => "jpeg"
    };

    private static string NormalizeFormat(string? format) => format?.ToLowerInvariant() switch
    {
        "jpg" => "jpeg",
        "jpeg" => "jpeg",
        "png" => "png",
        _ => "jpeg"
    };
}
