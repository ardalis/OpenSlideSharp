using Microsoft.Extensions.Options;
using MultiSlideServer.Cache;
using OpenSlideSharp.BitmapExtensions;

namespace MultiSlideServer.Services;

public record TileGenerationProgress(string ImageName, int Level, int Col, int Row, int TotalTilesGenerated, int TotalTiles);
public record TileGenerationResult(int TilesGenerated, int TilesSkipped, TimeSpan Duration);

public class TileGeneratorService
{
    private readonly ImageProvider _imageProvider;
    private readonly ImagesOption _options;
    private readonly ILogger<TileGeneratorService> _logger;

    public TileGeneratorService(ImageProvider imageProvider, IOptions<ImagesOption> options, ILogger<TileGeneratorService> logger)
    {
        _imageProvider = imageProvider;
        _options = options.Value;
        _logger = logger;
    }

    public string GetTileCachePath(string imageName, int level, int col, int row, string format)
    {
        // Per-image subfolder structure: {cache}/{imageName}/{level}/{col}_{row}.{format}
        var fileExtension = TileFormatOptions.GetFileExtension(format);
        return Path.Combine(_options.TileCachePath, imageName, level.ToString(), $"{col}_{row}.{fileExtension}");
    }

    public bool TileExistsOnDisk(string imageName, int level, int col, int row, string format)
    {
        return File.Exists(GetTileCachePath(imageName, level, col, row, format));
    }

    public async Task<Stream?> GetCachedTileAsync(string imageName, int level, int col, int row, string format, CancellationToken ct = default)
    {
        var cachePath = GetTileCachePath(imageName, level, col, row, format);
        if (!File.Exists(cachePath))
            return null;

        var memoryStream = new MemoryStream();
        await using var fileStream = File.OpenRead(cachePath);
        await fileStream.CopyToAsync(memoryStream, ct);
        memoryStream.Position = 0;
        return memoryStream;
    }

    public async Task SaveTileToDiskAsync(string imageName, int level, int col, int row, Stream tileData, string format, CancellationToken ct = default)
    {
        var tilePath = GetTileCachePath(imageName, level, col, row, format);
        var directory = Path.GetDirectoryName(tilePath)!;
        Directory.CreateDirectory(directory);

        await using var fileStream = File.Create(tilePath);
        await tileData.CopyToAsync(fileStream, ct);
    }

    public async Task<TileGenerationResult> GenerateAllTilesForImageAsync(
        string imageName,
        bool overwrite = false,
        IProgress<TileGenerationProgress>? progress = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Starting tile generation for image '{ImageName}'", imageName);
        if (!_imageProvider.TryGetImagePath(imageName, out var imagePath))
            throw new ArgumentException($"Image '{imageName}' not found", nameof(imageName));

        var startTime = DateTime.UtcNow;
        var dz = _imageProvider.RetainDeepZoomGenerator(imageName, imagePath);
        var formatOptions = _options.TileFormat;

        try
        {
            var tilesGenerated = 0;
            var tilesSkipped = 0;
            var totalTiles = dz.TileCount;
            var processed = 0;

            var levelTiles = dz.LevelTiles.ToList();

            for (int level = 0; level < dz.LevelCount; level++)
            {
                var tileDims = levelTiles[level];
                
                // Determine format for this level
                var format = formatOptions.GetFormatForLevel(level, dz.LevelCount);

                for (int col = 0; col < tileDims.Cols; col++)
                {
                    for (int row = 0; row < tileDims.Rows; row++)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (!overwrite && TileExistsOnDisk(imageName, level, col, row, format))
                        {
                            tilesSkipped++;
                        }
                        else
                        {
                            // Generate tile in the appropriate format
                            using var tileStream = format == "png"
                                ? dz.GetTileAsPngStream(level, col, row, out _)
                                : dz.GetTileAsJpegStream(level, col, row, out _, formatOptions.JpegQuality);
                            
                            await SaveTileToDiskAsync(imageName, level, col, row, tileStream, format, ct);
                            tilesGenerated++;
                        }

                        processed++;
                        progress?.Report(new TileGenerationProgress(imageName, level, col, row, processed, totalTiles));
                    }
                }
            }

            return new TileGenerationResult(tilesGenerated, tilesSkipped, DateTime.UtcNow - startTime);
        }
        finally
        {
            dz.Release();
        }
    }

    public void ClearCacheForImage(string imageName)
    {
        var imageCachePath = Path.Combine(_options.TileCachePath, imageName);
        if (Directory.Exists(imageCachePath))
        {
            Directory.Delete(imageCachePath, recursive: true);
        }
    }

    public void ClearAllCache()
    {
        if (Directory.Exists(_options.TileCachePath))
        {
            Directory.Delete(_options.TileCachePath, recursive: true);
        }
    }

    public (int fileCount, long totalSize) GetCacheStatsForImage(string imageName)
    {
        var imageCachePath = Path.Combine(_options.TileCachePath, imageName);
        if (!Directory.Exists(imageCachePath))
            return (0, 0);

        // Count both jpeg and png files
        var jpegFiles = Directory.GetFiles(imageCachePath, "*.jpeg", SearchOption.AllDirectories);
        var pngFiles = Directory.GetFiles(imageCachePath, "*.png", SearchOption.AllDirectories);
        var allFiles = jpegFiles.Concat(pngFiles).ToArray();
        var totalSize = allFiles.Sum(f => new FileInfo(f).Length);
        return (allFiles.Length, totalSize);
    }

    public (int fileCount, long totalSize) GetTotalCacheStats()
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
