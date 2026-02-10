using Microsoft.Extensions.Options;
using OpenSlideSharp.BitmapExtensions;

namespace SingleSlideServer.Services;

public record TileGenerationProgress(int Level, int Col, int Row, int TotalTilesGenerated, int TotalTiles);
public record TileGenerationResult(int TilesGenerated, int TilesSkipped, TimeSpan Duration);

public class TileGeneratorService
{
    private readonly ImageProvider _imageProvider;
    private readonly ImageOption _options;

    public TileGeneratorService(ImageProvider imageProvider, IOptions<ImageOption> options)
    {
        _imageProvider = imageProvider;
        _options = options.Value;
    }

    public int GetTotalTileCount()
    {
        return _imageProvider.DeepZoomGenerator.TileCount;
    }

    public async Task<TileGenerationResult> GenerateAllTilesAsync(
        bool overwrite = false,
        IProgress<TileGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var dz = _imageProvider.DeepZoomGenerator;
        var tilesGenerated = 0;
        var tilesSkipped = 0;
        var totalTiles = dz.TileCount;
        var processed = 0;

        var levelTiles = dz.LevelTiles.ToList();
        var formatOptions = _options.TileFormat;

        for (int level = 0; level < dz.LevelCount; level++)
        {
            var tileDims = levelTiles[level];
            var cols = tileDims.Cols;
            var rows = tileDims.Rows;

            // Determine format for this level
            var format = formatOptions.GetFormatForLevel(level, dz.LevelCount);
            var fileExtension = TileFormatOptions.GetFileExtension(format);

            var levelPath = Path.Combine(_options.TileCachePath, level.ToString());
            Directory.CreateDirectory(levelPath);

            for (int col = 0; col < cols; col++)
            {
                for (int row = 0; row < rows; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var tilePath = Path.Combine(levelPath, $"{col}_{row}.{fileExtension}");

                    if (!overwrite && File.Exists(tilePath))
                    {
                        tilesSkipped++;
                    }
                    else
                    {
                        // Generate tile in the appropriate format
                        using var tileStream = format == "png"
                            ? dz.GetTileAsPngStream(level, col, row, out _)
                            : dz.GetTileAsJpegStream(level, col, row, out _, formatOptions.JpegQuality);
                        
                        await using var fileStream = File.Create(tilePath);
                        await tileStream.CopyToAsync(fileStream, cancellationToken);
                        tilesGenerated++;
                    }

                    processed++;
                    progress?.Report(new TileGenerationProgress(level, col, row, processed, totalTiles));
                }
            }
        }

        return new TileGenerationResult(tilesGenerated, tilesSkipped, DateTime.UtcNow - startTime);
    }

    public string GetTileCachePath(int level, int col, int row, string? format = null)
    {
        var dz = _imageProvider.DeepZoomGenerator;
        var effectiveFormat = format ?? _options.TileFormat.GetFormatForLevel(level, dz.LevelCount);
        var fileExtension = TileFormatOptions.GetFileExtension(effectiveFormat);
        return Path.Combine(_options.TileCachePath, level.ToString(), $"{col}_{row}.{fileExtension}");
    }

    public bool TileExistsOnDisk(int level, int col, int row)
    {
        return File.Exists(GetTileCachePath(level, col, row));
    }

    public async Task SaveTileToDiskAsync(int level, int col, int row, Stream tileData, string? format = null, CancellationToken cancellationToken = default)
    {
        var tilePath = GetTileCachePath(level, col, row, format);
        var directory = Path.GetDirectoryName(tilePath)!;
        Directory.CreateDirectory(directory);

        await using var fileStream = File.Create(tilePath);
        await tileData.CopyToAsync(fileStream, cancellationToken);
    }

    public void ClearCache()
    {
        if (Directory.Exists(_options.TileCachePath))
        {
            Directory.Delete(_options.TileCachePath, recursive: true);
        }
    }

    public (int fileCount, long totalSize) GetCacheStats()
    {
        if (!Directory.Exists(_options.TileCachePath))
            return (0, 0);

        // Count both jpeg and png files
        var jpegFiles = Directory.GetFiles(_options.TileCachePath, "*.jpeg", SearchOption.AllDirectories);
        var pngFiles = Directory.GetFiles(_options.TileCachePath, "*.png", SearchOption.AllDirectories);
        var allFiles = jpegFiles.Concat(pngFiles).ToArray();
        var totalSize = allFiles.Sum(f => new FileInfo(f).Length);
        return (allFiles.Length, totalSize);
    }
}
