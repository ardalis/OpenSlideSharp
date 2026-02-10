# Plan: Disk Tile Caching for SingleSlideServer

## Overview

Add disk-based tile caching to SingleSlideServer with two components:
1. A new endpoint to pre-generate tiles to disk
2. Update `/image_files` to serve from disk cache when available, falling back to on-the-fly generation

## Current State

- `/image_files/{level}/{col}_{row}.jpeg` generates tiles on-the-fly via `DeepZoomGenerator.GetTileAsJpegStream()`
- No disk persistence - every request re-reads from the slide file and encodes JPEG
- Works but may be slow for large slides or high traffic

## Proposed Changes

### 1. Configuration

Add settings to `appsettings.json`:

```json
{
  "Image": {
    "Path": "image.tiff",
    "TileCachePath": "./tile_cache",
    "EnableDiskCache": true
  }
}
```

Update `ImageOption.cs`:

```csharp
public class ImageOption
{
    public string Path { get; set; }
    public string TileCachePath { get; set; } = "./tile_cache";
    public bool EnableDiskCache { get; set; } = true;
}
```

### 2. New Endpoint: Generate Tiles

**Endpoint:** `POST /generate-tiles` or `POST /api/tiles/generate`

**Purpose:** Pre-generate all tiles for all zoom levels and save to disk

**Implementation:**
- Iterate through all levels from `DeepZoomGenerator`
- For each level, iterate through all tile coordinates
- Generate each tile and save to: `{TileCachePath}/{level}/{col}_{row}.jpeg`
- Return progress/status (consider making this a background job for large slides)

**Options:**
- `?levels=0,1,2` - Generate only specific levels
- `?overwrite=true` - Regenerate existing tiles

**Response:**
```json
{
  "status": "completed",
  "tilesGenerated": 1234,
  "duration": "00:02:34",
  "cachePath": "./tile_cache"
}
```

### 3. Update `/image_files` Endpoint

Modify the tile-serving middleware in `Program.cs`:

```csharp
app.Map("/image_files", appBuilder =>
{
    appBuilder.Use(async (context, next) =>
    {
        if (!TryParseDeepZoom(context.Request.Path, out var result))
        {
            await next();
            return;
        }

        if (result.format != "jpeg")
        {
            await next();
            return;
        }

        var options = context.RequestServices.GetService<IOptions<ImageOption>>()!.Value;
        var response = context.Response;
        response.ContentType = "image/jpeg";

        // Check disk cache first
        if (options.EnableDiskCache)
        {
            var cachePath = Path.Combine(
                options.TileCachePath, 
                result.level.ToString(), 
                $"{result.col}_{result.row}.jpeg");
            
            if (File.Exists(cachePath))
            {
                await using var fileStream = File.OpenRead(cachePath);
                await fileStream.CopyToAsync(response.Body);
                return;
            }
        }

        // Fall back to on-the-fly generation
        var provider = context.RequestServices.GetService<ImageProvider>()!;
        var tileStream = provider.DeepZoomGenerator.GetTileAsJpegStream(
            result.level, result.col, result.row, out var tmp);
        
        // Optionally save to disk for future requests
        if (options.EnableDiskCache)
        {
            var cachePath = Path.Combine(
                options.TileCachePath, 
                result.level.ToString(), 
                $"{result.col}_{result.row}.jpeg");
            
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            
            // Copy to both response and file
            using var memoryStream = new MemoryStream();
            await tileStream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;
            
            await using (var fileStream = File.Create(cachePath))
            {
                await memoryStream.CopyToAsync(fileStream);
            }
            
            memoryStream.Position = 0;
            await memoryStream.CopyToAsync(response.Body);
        }
        else
        {
            await tileStream.CopyToAsync(response.Body);
        }
    });
});
```

### 4. Tile Generation Service (Optional)

Create `TileGeneratorService.cs` for the generation logic:

```csharp
public class TileGeneratorService
{
    private readonly ImageProvider _imageProvider;
    private readonly ImageOption _options;

    public async Task<TileGenerationResult> GenerateAllTilesAsync(
        IProgress<TileGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var dz = _imageProvider.DeepZoomGenerator;
        var totalTiles = 0;
        
        for (int level = 0; level < dz.LevelCount; level++)
        {
            var (cols, rows) = dz.GetTileCount(level);
            
            for (int col = 0; col < cols; col++)
            {
                for (int row = 0; row < rows; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    var cachePath = Path.Combine(
                        _options.TileCachePath,
                        level.ToString(),
                        $"{col}_{row}.jpeg");
                    
                    if (!File.Exists(cachePath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                        
                        using var tileStream = dz.GetTileAsJpegStream(level, col, row, out _);
                        await using var fileStream = File.Create(cachePath);
                        await tileStream.CopyToAsync(fileStream, cancellationToken);
                    }
                    
                    totalTiles++;
                    progress?.Report(new TileGenerationProgress(level, col, row, totalTiles));
                }
            }
        }
        
        return new TileGenerationResult(totalTiles);
    }
}
```

### 5. Controller for Generation Endpoint

Create `TilesController.cs`:

```csharp
[ApiController]
[Route("api/[controller]")]
public class TilesController : ControllerBase
{
    private readonly TileGeneratorService _generator;

    [HttpPost("generate")]
    public async Task<IActionResult> GenerateTiles(CancellationToken cancellationToken)
    {
        var result = await _generator.GenerateAllTilesAsync(cancellationToken: cancellationToken);
        return Ok(result);
    }
}
```

## File Structure After Implementation

```
SingleSlideServer/
├── Program.cs              # Updated middleware
├── ImageOption.cs          # Add cache settings
├── ImageProvider.cs        # No changes
├── Services/
│   └── TileGeneratorService.cs
├── Controllers/
│   ├── HomeController.cs   # Existing
│   └── TilesController.cs  # New
└── tile_cache/             # Generated at runtime
    ├── 0/
    │   └── 0_0.jpeg
    ├── 1/
    │   ├── 0_0.jpeg
    │   └── 0_1.jpeg
    └── ...
```

## Implementation Order

1. [ ] Update `ImageOption.cs` with new properties
2. [ ] Update `appsettings.json` with default values
3. [ ] Create `TileGeneratorService.cs`
4. [ ] Create `TilesController.cs` with generate endpoint
5. [ ] Update `Program.cs` middleware to check disk cache
6. [ ] Register new services in `Program.cs`
7. [ ] Update `README.md` with new features
8. [ ] Test with sample image

## Considerations

- **Large slides**: Generation could take minutes - consider background job with progress reporting
- **Disk space**: Full tile generation can use significant disk space
- **Concurrency**: Multiple requests for same uncached tile could cause race conditions - consider locking
- **Cache invalidation**: Add endpoint to clear cache when source image changes
- **Partial generation**: Allow generating only certain zoom levels (higher levels = more tiles)
