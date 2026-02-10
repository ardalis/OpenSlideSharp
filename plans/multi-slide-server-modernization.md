# MultiSlideServer Modernization Plan

## Overview

Modernize the `MultiSlideServer` example to use:
1. **Single-file Program.cs** (remove Startup.cs)
2. **Minimal APIs** (remove Controllers)
3. **Disk-based tile caching** with per-image subfolders

## Current Architecture

### Files to Modify/Remove
- `Program.cs` - Complete rewrite
- `Startup.cs` - **DELETE** (merge into Program.cs)
- `Controllers/HomeController.cs` - **DELETE** (convert to minimal API endpoints)
- `ImagesOption.cs` - Update with cache configuration
- `ImageProvider.cs` - Update with cache-aware methods
- `appsettings.json` - Add cache configuration

### Files to Keep (with potential updates)
- `Cache/DeepZoomGeneratorCache.cs` - Keep as-is (in-memory generator cache)
- `Cache/RetainableDeepZoomGenerator.cs` - Keep as-is
- `Views/` folder - **DELETE** (replace with static HTML generation)
- `wwwroot/` - Keep static assets

### New Files to Create
- `Services/TileGeneratorService.cs` - Tile generation and caching service (per-image aware)

---

## Implementation Steps

### Phase 1: Update Configuration

#### 1.1 Update `ImagesOption.cs`

Add disk cache configuration options:

```csharp
namespace MultiSlideServer
{
    public class ImagesOption
    {
        public ImageOptionItem[] Images { get; set; } = Array.Empty<ImageOptionItem>();
        public string TileCachePath { get; set; } = "./tile_cache";
        public bool EnableDiskCache { get; set; } = true;
    }

    public class ImageOptionItem
    {
        public string Name { get; set; } = null!;
        public string Path { get; set; } = null!;
    }
}
```

#### 1.2 Update `appsettings.json`

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  },
  "AllowedHosts": "*",
  "TileCachePath": "./tile_cache",
  "EnableDiskCache": true,
  "Images": [
    {
      "Name": "test1",
      "Path": "image1.tiff"
    },
    {
      "Name": "test2",
      "Path": "image2.tiff"
    }
  ]
}
```

---

### Phase 2: Create Tile Generator Service

#### 2.1 Create `Services/TileGeneratorService.cs`

Key differences from SingleSlideServer:
- **Per-image cache folders**: `{TileCachePath}/{imageName}/{level}/{col}_{row}.jpeg`
- Methods to work with named images

```csharp
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

    public TileGeneratorService(ImageProvider imageProvider, IOptions<ImagesOption> options)
    {
        _imageProvider = imageProvider;
        _options = options.Value;
    }

    public string GetTileCachePath(string imageName, int level, int col, int row)
    {
        // Per-image subfolder structure: {cache}/{imageName}/{level}/{col}_{row}.jpeg
        return Path.Combine(_options.TileCachePath, imageName, level.ToString(), $"{col}_{row}.jpeg");
    }

    public bool TileExistsOnDisk(string imageName, int level, int col, int row)
    {
        return File.Exists(GetTileCachePath(imageName, level, col, row));
    }

    public async Task<Stream?> GetCachedTileAsync(string imageName, int level, int col, int row, CancellationToken ct = default)
    {
        var cachePath = GetTileCachePath(imageName, level, col, row);
        if (!File.Exists(cachePath))
            return null;

        var memoryStream = new MemoryStream();
        await using var fileStream = File.OpenRead(cachePath);
        await fileStream.CopyToAsync(memoryStream, ct);
        memoryStream.Position = 0;
        return memoryStream;
    }

    public async Task SaveTileToDiskAsync(string imageName, int level, int col, int row, Stream tileData, CancellationToken ct = default)
    {
        var tilePath = GetTileCachePath(imageName, level, col, row);
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
        if (!_imageProvider.TryGetImagePath(imageName, out var imagePath))
            throw new ArgumentException($"Image '{imageName}' not found", nameof(imageName));

        var startTime = DateTime.UtcNow;
        var dz = _imageProvider.RetainDeepZoomGenerator(imageName, imagePath);
        
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

                for (int col = 0; col < tileDims.Cols; col++)
                {
                    for (int row = 0; row < tileDims.Rows; row++)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (!overwrite && TileExistsOnDisk(imageName, level, col, row))
                        {
                            tilesSkipped++;
                        }
                        else
                        {
                            using var tileStream = dz.GetTileAsJpegStream(level, col, row, out _);
                            await SaveTileToDiskAsync(imageName, level, col, row, tileStream, ct);
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

        var files = Directory.GetFiles(imageCachePath, "*.jpeg", SearchOption.AllDirectories);
        var totalSize = files.Sum(f => new FileInfo(f).Length);
        return (files.Length, totalSize);
    }

    public (int fileCount, long totalSize) GetTotalCacheStats()
    {
        if (!Directory.Exists(_options.TileCachePath))
            return (0, 0);

        var files = Directory.GetFiles(_options.TileCachePath, "*.jpeg", SearchOption.AllDirectories);
        var totalSize = files.Sum(f => new FileInfo(f).Length);
        return (files.Length, totalSize);
    }
}
```

---

### Phase 3: Update ImageProvider

Update `ImageProvider.cs` to expose cache options:

```csharp
using Microsoft.Extensions.Options;
using MultiSlideServer.Cache;
using OpenSlideSharp;

namespace MultiSlideServer;

public class ImageProvider
{
    private readonly ImageOptionItem[] _images;
    private readonly DeepZoomGeneratorCache _cache;
    private readonly ImagesOption _options;

    public ImageProvider(IOptions<ImagesOption> options, DeepZoomGeneratorCache cache)
    {
        _options = options.Value;
        _images = _options.Images;
        _cache = cache;
    }

    public IReadOnlyList<ImageOptionItem> Images => _images;
    public DeepZoomGeneratorCache Cache => _cache;
    public bool EnableDiskCache => _options.EnableDiskCache;
    public string TileCachePath => _options.TileCachePath;

    public bool TryGetImagePath(string name, out string path)
    {
        foreach (var item in _images)
        {
            if (item.Name == name)
            {
                path = item.Path;
                return true;
            }
        }
        path = null!;
        return false;
    }

    public RetainableDeepZoomGenerator RetainDeepZoomGenerator(string name, string path)
    {
        if (_cache.TryGet(name, out var dz))
        {
            dz.Retain();
            return dz;
        }
        
        dz = new RetainableDeepZoomGenerator(OpenSlideImage.Open(path));
        if (_cache.TrySet(name, dz))
        {
            dz.Retain();
            return dz;
        }
        
        dz.Retain();
        dz.Dispose();
        return dz;
    }
}
```

---

### Phase 4: Rewrite Program.cs with Minimal APIs

Replace `Program.cs` and `Startup.cs` with a single minimal API file:

```csharp
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MultiSlideServer;
using MultiSlideServer.Cache;
using MultiSlideServer.Services;
using OpenSlideSharp.BitmapExtensions;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Configure services
builder.Services.Configure<ImagesOption>(builder.Configuration);
builder.Services.AddSingleton<DeepZoomGeneratorCache>();
builder.Services.AddSingleton<ImageProvider>();
builder.Services.AddSingleton<TileGeneratorService>();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// Tile serving middleware with disk caching
app.Map("/storage", appBuilder =>
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

        var provider = context.RequestServices.GetRequiredService<ImageProvider>();
        var tileService = context.RequestServices.GetRequiredService<TileGeneratorService>();
        var response = context.Response;

        if (!provider.TryGetImagePath(result.name, out string path))
        {
            response.StatusCode = 404;
            await response.WriteAsync("Image not found.");
            return;
        }

        response.ContentType = "image/jpeg";

        // Check disk cache first
        if (provider.EnableDiskCache)
        {
            var cachedStream = await tileService.GetCachedTileAsync(
                result.name, result.level, result.col, result.row);
            
            if (cachedStream != null)
            {
                await using (cachedStream)
                {
                    await cachedStream.CopyToAsync(response.Body);
                }
                return;
            }
        }

        // Generate tile on-the-fly
        var dz = provider.RetainDeepZoomGenerator(result.name, path);
        try
        {
            using var tileStream = dz.GetTileAsJpegStream(result.level, result.col, result.row, out _);

            if (provider.EnableDiskCache)
            {
                // Read into memory so we can write to both file and response
                using var memoryStream = new MemoryStream();
                await tileStream.CopyToAsync(memoryStream);

                // Save to disk
                memoryStream.Position = 0;
                await tileService.SaveTileToDiskAsync(
                    result.name, result.level, result.col, result.row, memoryStream);

                // Send to response
                memoryStream.Position = 0;
                await memoryStream.CopyToAsync(response.Body);
            }
            else
            {
                await tileStream.CopyToAsync(response.Body);
            }
        }
        finally
        {
            dz.Release();
        }
    });
});

// ============================================
// Minimal API Endpoints (replacing Controllers)
// ============================================

// Home page - list all images
app.MapGet("/", (ImageProvider provider) =>
{
    var html = new StringBuilder();
    html.AppendLine("<!DOCTYPE html>");
    html.AppendLine("<html lang=\"en\">");
    html.AppendLine("<head>");
    html.AppendLine("    <meta charset=\"UTF-8\">");
    html.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
    html.AppendLine("    <title>Multi Slide Server</title>");
    html.AppendLine("</head>");
    html.AppendLine("<body>");
    html.AppendLine("    <h1>Available Slides</h1>");
    html.AppendLine("    <ul>");
    
    foreach (var image in provider.Images)
    {
        var encodedName = Uri.EscapeDataString(image.Name);
        html.AppendLine($"        <li><a href=\"/slide/{encodedName}.html\">{image.Name}</a></li>");
    }
    
    html.AppendLine("    </ul>");
    html.AppendLine("</body>");
    html.AppendLine("</html>");
    
    return Results.Content(html.ToString(), "text/html");
});

// Slide viewer page
app.MapGet("/slide/{name}.html", (string name, ImageProvider provider) =>
{
    if (!provider.TryGetImagePath(name, out _))
    {
        return Results.NotFound();
    }

    var html = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>{name} - Multi Slide Server</title>
</head>
<body>
    <div id=""openseadragon1"" style=""position:fixed; left: 0; right: 0; top: 0; bottom: 0;""></div>
    <script src=""/openseadragon.js""></script>
    <script type=""text/javascript"">
        var viewer = OpenSeadragon({{
            id: ""openseadragon1"",
            prefixUrl: ""/images/"",
            tileSources: ""/storage/{name}.dzi""
        }});
    </script>
</body>
</html>";

    return Results.Content(html, "text/html");
});

// Deep Zoom Image descriptor for specific image
app.MapGet("/storage/{name}.dzi", (string name, ImageProvider provider) =>
{
    if (!provider.TryGetImagePath(name, out string path))
    {
        return Results.NotFound();
    }

    var dz = provider.RetainDeepZoomGenerator(name, path);
    try
    {
        return Results.Content(dz.GetDzi(), "application/xml", Encoding.UTF8);
    }
    finally
    {
        dz.Release();
    }
});

// ============================================
// Tile Generation API Endpoints
// ============================================

// Generate all tiles for a specific image
app.MapPost("/api/tiles/{imageName}/generate", async (
    string imageName,
    TileGeneratorService generator,
    bool? overwrite,
    CancellationToken ct) =>
{
    try
    {
        var result = await generator.GenerateAllTilesForImageAsync(
            imageName, overwrite ?? false, cancellationToken: ct);
        
        return Results.Ok(new
        {
            status = "completed",
            imageName,
            tilesGenerated = result.TilesGenerated,
            tilesSkipped = result.TilesSkipped,
            duration = result.Duration.ToString(@"hh\:mm\:ss\.fff")
        });
    }
    catch (ArgumentException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

// Get cache stats for a specific image
app.MapGet("/api/tiles/{imageName}/stats", (string imageName, TileGeneratorService generator, ImageProvider provider) =>
{
    if (!provider.TryGetImagePath(imageName, out var path))
    {
        return Results.NotFound(new { error = $"Image '{imageName}' not found" });
    }

    var (fileCount, totalSize) = generator.GetCacheStatsForImage(imageName);
    var dz = provider.RetainDeepZoomGenerator(imageName, path);
    int totalTiles;
    try
    {
        totalTiles = dz.TileCount;
    }
    finally
    {
        dz.Release();
    }

    return Results.Ok(new
    {
        imageName,
        cachedTiles = fileCount,
        totalTiles,
        cachePercentage = totalTiles > 0 ? Math.Round((double)fileCount / totalTiles * 100, 2) : 0,
        cacheSizeBytes = totalSize,
        cacheSizeMB = Math.Round(totalSize / (1024.0 * 1024.0), 2)
    });
});

// Clear cache for a specific image
app.MapDelete("/api/tiles/{imageName}/cache", (string imageName, TileGeneratorService generator, ImageProvider provider) =>
{
    if (!provider.TryGetImagePath(imageName, out _))
    {
        return Results.NotFound(new { error = $"Image '{imageName}' not found" });
    }

    generator.ClearCacheForImage(imageName);
    return Results.Ok(new { status = "cache cleared", imageName });
});

// Get total cache stats across all images
app.MapGet("/api/tiles/stats", (TileGeneratorService generator) =>
{
    var (fileCount, totalSize) = generator.GetTotalCacheStats();
    return Results.Ok(new
    {
        cachedTiles = fileCount,
        cacheSizeBytes = totalSize,
        cacheSizeMB = Math.Round(totalSize / (1024.0 * 1024.0), 2)
    });
});

// Clear all cache
app.MapDelete("/api/tiles/cache", (TileGeneratorService generator) =>
{
    generator.ClearAllCache();
    return Results.Ok(new { status = "all cache cleared" });
});

// List all images with their cache status
app.MapGet("/api/images", (ImageProvider provider, TileGeneratorService generator) =>
{
    var images = provider.Images.Select(img =>
    {
        var (fileCount, totalSize) = generator.GetCacheStatsForImage(img.Name);
        return new
        {
            name = img.Name,
            path = img.Path,
            cachedTiles = fileCount,
            cacheSizeMB = Math.Round(totalSize / (1024.0 * 1024.0), 2)
        };
    });

    return Results.Ok(images);
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();

// ============================================
// Helper Functions
// ============================================

// expression: /{name}_files/{level}/{col}_{row}.jpeg
static bool TryParseDeepZoom(string expression, out (string name, int level, int col, int row, string format) result)
{
    if (expression.Length < 4 || expression[0] != '/')
    {
        result = default;
        return false;
    }

    // seg: {name}_files/{level}/{col}_{row}.jpeg
    StringSegment seg = new StringSegment(expression, 1, expression.Length - 1);
    int iPos = seg.IndexOf('/');
    if (iPos <= 0)
    {
        result = default;
        return false;
    }

    StringSegment segName = seg.Subsegment(0, iPos);
    if (segName.Length < 6 || !segName.EndsWith("_files", StringComparison.Ordinal))
    {
        result = default;
        return false;
    }
    string resultName = segName.Substring(0, segName.Length - 6);

    // seg: {level}/{col}_{row}.jpeg
    seg = seg.Subsegment(iPos + 1);
    iPos = seg.IndexOf('/');
    if (iPos <= 0)
    {
        result = default;
        return false;
    }

    if (!int.TryParse(seg.Substring(0, iPos), out var resultLevel))
    {
        result = default;
        return false;
    }

    // seg: {col}_{row}.jpeg
    seg = seg.Subsegment(iPos + 1);
    iPos = seg.IndexOf('_');
    if (seg.IndexOf('/') >= 0 || iPos <= 0)
    {
        result = default;
        return false;
    }

    if (!int.TryParse(seg.Substring(0, iPos), out var resultCol))
    {
        result = default;
        return false;
    }

    // seg: {row}.jpeg
    seg = seg.Subsegment(iPos + 1);
    iPos = seg.IndexOf('.');
    if (iPos <= 0)
    {
        result = default;
        return false;
    }

    if (!int.TryParse(seg.Substring(0, iPos), out var resultRow))
    {
        result = default;
        return false;
    }

    // seg: jpeg
    seg = seg.Subsegment(iPos + 1);

    result = (name: resultName, level: resultLevel, col: resultCol, row: resultRow, format: seg.ToString());
    return true;
}
```

---

### Phase 5: Cleanup

#### 5.1 Delete Files
- `Startup.cs`
- `Controllers/HomeController.cs`
- `Controllers/` folder (if empty)
- `Views/Home/Index.cshtml`
- `Views/Home/Slide.cshtml`
- `Views/` folder (if empty)

#### 5.2 Update Project File

Update `MultiSlideServer.csproj` if needed to:
- Remove MVC-related packages if not needed
- Ensure minimal API compatibility
- Update target framework if necessary

---

## Disk Cache Structure

### SingleSlideServer (Current)
```
tile_cache/
├── 0/
│   └── 0_0.jpeg
├── 1/
│   ├── 0_0.jpeg
│   └── 0_1.jpeg
└── ...
```

### MultiSlideServer (New - Per-Image Subfolders)
```
tile_cache/
├── image1/
│   ├── 0/
│   │   └── 0_0.jpeg
│   ├── 1/
│   │   ├── 0_0.jpeg
│   │   └── 0_1.jpeg
│   └── ...
├── image2/
│   ├── 0/
│   │   └── 0_0.jpeg
│   └── ...
└── ...
```

---

## API Endpoints Summary

### Page Endpoints
| Method | Path | Description |
|--------|------|-------------|
| GET | `/` | Home page listing all images |
| GET | `/slide/{name}.html` | Viewer page for specific image |

### Deep Zoom Endpoints
| Method | Path | Description |
|--------|------|-------------|
| GET | `/storage/{name}.dzi` | DZI descriptor for image |
| GET | `/storage/{name}_files/{level}/{col}_{row}.jpeg` | Tile image (with caching) |

### Management API Endpoints
| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/images` | List all images with cache status |
| GET | `/api/tiles/stats` | Total cache statistics |
| DELETE | `/api/tiles/cache` | Clear all cache |
| POST | `/api/tiles/{imageName}/generate` | Pre-generate tiles for image |
| GET | `/api/tiles/{imageName}/stats` | Cache stats for specific image |
| DELETE | `/api/tiles/{imageName}/cache` | Clear cache for specific image |

---

## Testing Checklist

- [ ] Verify all images are listed on home page
- [ ] Verify slide viewer loads for each image
- [ ] Verify tiles are served correctly
- [ ] Verify tiles are cached to disk in per-image subfolders
- [ ] Verify cached tiles are served from disk on subsequent requests
- [ ] Verify tile generation API works for individual images
- [ ] Verify cache clear API works for individual images and globally
- [ ] Verify cache statistics are accurate
- [ ] Test with `EnableDiskCache = false` to ensure fallback works
- [ ] Performance test with multiple concurrent image viewers

---

## Migration Notes

1. **Breaking Change**: Views are removed; HTML is now generated inline. This simplifies deployment but loses Razor flexibility.

2. **Configuration**: The configuration structure remains compatible but adds new cache-related options at the root level.

3. **Memory Cache**: The in-memory `DeepZoomGeneratorCache` is retained for caching OpenSlide image handles. The disk cache is a separate layer for tile data.

4. **Thread Safety**: The `RetainableDeepZoomGenerator` pattern is maintained for safe concurrent access to OpenSlide images.

5. **Backward Compatibility**: The URL structure (`/storage/{name}.dzi` and `/storage/{name}_files/...`) remains unchanged for client compatibility.
