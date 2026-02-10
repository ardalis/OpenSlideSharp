# Plan: Lossless Tile Format Support for Regulatory Compliance

## Status: ✅ IMPLEMENTED

**Implementation:** Option A (PNG at highest resolution) with Option B configuration framework.

## Overview

For regulatory compliance in medical/diagnostic imaging, the highest resolution tiles (and optionally all zoom levels) must preserve the original image data with **zero loss**. JPEG encoding is inherently lossy, so we need to support lossless image formats for tile serving and caching.

## Background: Why JPEG is Always Lossy

JPEG uses Discrete Cosine Transform (DCT) and quantization, which **always** discards some image data—even at quality 100. This is by design for compression efficiency but is unacceptable for regulatory scenarios requiring bit-perfect fidelity to the original slide data.

## Lossless Web Format Options

| Format | Lossless | Browser Support | Compression | Notes |
|--------|----------|-----------------|-------------|-------|
| **PNG** | ✅ Yes | ✅ Universal | Moderate | Best compatibility; ~3-5x larger than JPEG |
| **WebP (lossless)** | ✅ Yes | ✅ Modern browsers (95%+) | Good | 20-30% smaller than PNG; requires explicit lossless flag |
| **AVIF (lossless)** | ✅ Yes | ⚠️ Growing (~85%) | Excellent | Best compression but slower encode; Safari/Edge support improving |
| **TIFF** | ✅ Yes | ❌ Native browser | Variable | Not suitable for web delivery |

### Recommendation

**Primary: PNG** - Universal browser support, guaranteed lossless, well-understood  
**Secondary: WebP (lossless mode)** - Better compression, modern browser support  
**Future: AVIF** - Best compression, monitor browser adoption

---

## Implementation Options

### Option A: PNG-Only at Highest Resolution (Minimal Change)

**Scope:** Serve PNG only for the highest zoom level (deepest level = original resolution tiles)

**Pros:**
- Minimal code changes
- Only affects tiles where lossless matters most
- Lower bandwidth/storage impact than full PNG

**Configuration:**
```json
{
  "Image": {
    "TileFormat": "jpeg",
    "HighestResolutionFormat": "png"
  }
}
```

**Logic:**
```csharp
bool isHighestLevel = (level == dz.LevelCount - 1);
string format = isHighestLevel ? options.HighestResolutionFormat : options.TileFormat;
```

---

### Option B: Configurable Format Per Level Range (Flexible)

**Scope:** Configure which levels use lossless vs lossy formats

**Configuration:**
```json
{
  "Image": {
    "TileFormat": {
      "Default": "jpeg",
      "LosslessFromLevel": -3,  // Top 3 levels use PNG (negative = from end)
      "LosslessFormat": "png"
    }
  }
}
```

Or explicit level mapping:
```json
{
  "Image": {
    "TileFormatByLevel": {
      "default": "jpeg",
      "levels": {
        "15": "png",
        "16": "png",
        "17": "png"
      }
    }
  }
}
```

---

### Option C: Full Lossless Support (All Levels)

**Scope:** Serve PNG (or WebP lossless) for all tiles

**Pros:**
- Complete fidelity at all zoom levels
- Simplest logic (no level-checking)

**Cons:**
- 3-5x larger file sizes
- Higher bandwidth and storage costs
- Slower initial page loads at lower zoom levels

**Configuration:**
```json
{
  "Image": {
    "TileFormat": "png"
  }
}
```

---

### Option D: Client-Negotiated Format (Advanced)

**Scope:** Let clients request their preferred format via Accept header or query parameter

**Request Examples:**
```
GET /image_files/17/5_3.png           # Explicit PNG
GET /image_files/17/5_3.webp          # Explicit WebP  
GET /image_files/17/5_3?format=png    # Query parameter
Accept: image/png, image/webp         # Content negotiation
```

**Pros:**
- Maximum flexibility
- Clients can choose based on their needs
- Regulatory clients get lossless; casual viewers get efficient JPEG

**Cons:**
- More complex implementation
- Multiple cache files per tile (jpeg + png)

---

## Recommended Implementation: Option B with Content Negotiation

Combine **Option B** (level-based configuration) with **Option D** (client negotiation) for maximum flexibility:

1. **Default behavior:** Configurable per-level format (e.g., top N levels = PNG)
2. **Override:** Client can request specific format via URL extension (`.png`, `.webp`, `.jpeg`)
3. **Cache both:** When disk caching is enabled, cache all requested formats

---

## Implementation Details

### 1. Configuration Changes

**SingleSlideServer `ImageOption.cs`:**
```csharp
public class ImageOption
{
    public string Path { get; set; }
    public string TileCachePath { get; set; } = "./tile_cache";
    public bool EnableDiskCache { get; set; } = true;
    
    // New: Format configuration
    public TileFormatOptions TileFormat { get; set; } = new();
}

public class TileFormatOptions
{
    /// <summary>
    /// Default format for tiles: "jpeg", "png", "webp"
    /// </summary>
    public string Default { get; set; } = "jpeg";
    
    /// <summary>
    /// Format for high-resolution tiles: "png", "webp", or null to use Default
    /// </summary>
    public string? LosslessFormat { get; set; } = "png";
    
    /// <summary>
    /// Number of highest-resolution levels to serve as lossless.
    /// 0 = none, 1 = only highest, -1 = all levels, 3 = top 3 levels
    /// </summary>
    public int LosslessLevelCount { get; set; } = 1;
    
    /// <summary>
    /// JPEG quality (1-100) when using JPEG format
    /// </summary>
    public int JpegQuality { get; set; } = 90;
    
    /// <summary>
    /// Allow clients to override format via URL extension
    /// </summary>
    public bool AllowFormatOverride { get; set; } = true;
}
```

**MultiSlideServer `ImagesOption.cs`:**
```csharp
public class ImagesOption
{
    public List<ImageEntry> Images { get; set; } = new();
    public string TileCachePath { get; set; } = "./tile_cache";
    public bool EnableDiskCache { get; set; } = true;
    public TileFormatOptions TileFormat { get; set; } = new();
}
```

**Example `appsettings.json`:**
```json
{
  "Image": {
    "Path": "slide.svs",
    "TileCachePath": "./tile_cache",
    "EnableDiskCache": true,
    "TileFormat": {
      "Default": "jpeg",
      "LosslessFormat": "png",
      "LosslessLevelCount": 1,
      "JpegQuality": 90,
      "AllowFormatOverride": true
    }
  }
}
```

---

### 2. Core Library Extensions

**Add to `DeepZoomGeneratorExtensions.cs`:**

The library already has `GetTileAsPng()` and `GetTileAsPngStream()` methods! We need to add WebP support and a unified method:

```csharp
public enum TileFormat
{
    Jpeg,
    Png,
    WebP
}

public static MemoryStream GetTileAsStream(
    this DeepZoomGenerator generator, 
    int level, int col, int row, 
    out TileInfo tileInfo, 
    TileFormat format = TileFormat.Jpeg,
    int? quality = null)
{
    return format switch
    {
        TileFormat.Png => GetTileAsPngStream(generator, level, col, row, out tileInfo, quality),
        TileFormat.WebP => GetTileAsWebPStream(generator, level, col, row, out tileInfo, quality),
        _ => GetTileAsJpegStream(generator, level, col, row, out tileInfo, quality)
    };
}

// WebP support requires additional dependency (ImageSharp or SkiaSharp)
public static MemoryStream GetTileAsWebPStream(
    this DeepZoomGenerator generator, 
    int level, int col, int row, 
    out TileInfo tileInfo, 
    int? quality = null)
{
    // Implementation depends on chosen library
    throw new NotImplementedException("WebP support requires ImageSharp or SkiaSharp");
}
```

---

### 3. Endpoint Updates

**Update URL pattern to support format extension:**

Current: `/image_files/{level}/{col}_{row}.jpeg`  
New: `/image_files/{level}/{col}_{row}.{format}` where format = jpeg|png|webp

**Updated `TryParseDeepZoom`:**
```csharp
static bool TryParseDeepZoom(PathString path, out (int level, int col, int row, string format) result)
{
    // Parse: /{level}/{col}_{row}.{format}
    // Accept: jpeg, jpg, png, webp
}
```

**Updated tile-serving logic:**
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

        var options = context.RequestServices.GetService<IOptions<ImageOption>>()!.Value;
        var provider = context.RequestServices.GetService<ImageProvider>()!;
        var dz = provider.DeepZoomGenerator;

        // Determine effective format
        string effectiveFormat = DetermineFormat(
            requestedFormat: result.format,
            level: result.level,
            totalLevels: dz.LevelCount,
            options: options.TileFormat);

        var response = context.Response;
        response.ContentType = effectiveFormat switch
        {
            "png" => "image/png",
            "webp" => "image/webp",
            _ => "image/jpeg"
        };

        // Check disk cache
        var cachePath = GetCachePath(options, result.level, result.col, result.row, effectiveFormat);
        if (options.EnableDiskCache && File.Exists(cachePath))
        {
            await response.SendFileAsync(cachePath);
            return;
        }

        // Generate tile in requested format
        using var tileStream = effectiveFormat switch
        {
            "png" => dz.GetTileAsPngStream(result.level, result.col, result.row, out _),
            "webp" => dz.GetTileAsWebPStream(result.level, result.col, result.row, out _),
            _ => dz.GetTileAsJpegStream(result.level, result.col, result.row, out _, options.TileFormat.JpegQuality)
        };

        // Cache to disk if enabled
        if (options.EnableDiskCache)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await using var fs = File.Create(cachePath);
            await tileStream.CopyToAsync(fs);
            tileStream.Position = 0;
        }

        await tileStream.CopyToAsync(response.Body);
    });
});
```

**Format determination helper:**
```csharp
static string DetermineFormat(string requestedFormat, int level, int totalLevels, TileFormatOptions options)
{
    // If format override is allowed and client requested specific format, use it
    if (options.AllowFormatOverride && !string.IsNullOrEmpty(requestedFormat))
    {
        return NormalizeFormat(requestedFormat);
    }

    // Check if this level should be lossless
    if (!string.IsNullOrEmpty(options.LosslessFormat))
    {
        bool useLossless = options.LosslessLevelCount switch
        {
            -1 => true,  // All levels
            0 => false,  // No lossless
            _ => level >= (totalLevels - options.LosslessLevelCount)  // Top N levels
        };
        
        if (useLossless)
            return NormalizeFormat(options.LosslessFormat);
    }

    return NormalizeFormat(options.Default);
}

static string NormalizeFormat(string format) => format?.ToLowerInvariant() switch
{
    "jpg" => "jpeg",
    "jpeg" => "jpeg",
    "png" => "png",
    "webp" => "webp",
    _ => "jpeg"
};
```

---

### 4. Cache Path Updates

**Update `TileGeneratorService.cs`:**
```csharp
public string GetTileCachePath(int level, int col, int row, string format = "jpeg")
{
    return Path.Combine(_options.TileCachePath, level.ToString(), $"{col}_{row}.{format}");
}
```

---

### 5. Tile Pre-generation Updates

**Update `GenerateAllTilesAsync` to support format configuration:**
```csharp
public async Task<TileGenerationResult> GenerateAllTilesAsync(
    bool overwrite = false,
    IProgress<TileGenerationProgress>? progress = null,
    CancellationToken cancellationToken = default)
{
    var dz = _imageProvider.DeepZoomGenerator;
    var formatOptions = _options.TileFormat;
    
    for (int level = 0; level < dz.LevelCount; level++)
    {
        string format = DetermineFormat(null, level, dz.LevelCount, formatOptions);
        
        for (int col = 0; col < cols; col++)
        {
            for (int row = 0; row < rows; row++)
            {
                var tilePath = GetTileCachePath(level, col, row, format);
                
                if (!overwrite && File.Exists(tilePath))
                {
                    tilesSkipped++;
                    continue;
                }
                
                using var tileStream = format switch
                {
                    "png" => dz.GetTileAsPngStream(level, col, row, out _),
                    "webp" => dz.GetTileAsWebPStream(level, col, row, out _),
                    _ => dz.GetTileAsJpegStream(level, col, row, out _, formatOptions.JpegQuality)
                };
                
                // Save to disk...
            }
        }
    }
}
```

---

## Storage and Bandwidth Considerations

### File Size Comparison (256x256 tile)

| Content Type | JPEG (q=90) | PNG | WebP Lossless |
|--------------|-------------|-----|---------------|
| Pathology H&E | ~15-25 KB | ~60-100 KB | ~40-70 KB |
| Low detail/background | ~5-10 KB | ~10-20 KB | ~8-15 KB |

### Storage Impact Example

For a 100,000 x 100,000 pixel slide at 256px tiles:
- Total tiles at highest level: ~153,000 tiles
- JPEG storage: ~3-4 GB
- PNG storage: ~12-15 GB
- WebP lossless: ~8-10 GB

**Recommendation:** Use lossless only for highest 1-3 levels to minimize storage impact while ensuring regulatory compliance at diagnostic resolution.

---

## Browser Compatibility

| Format | Chrome | Firefox | Safari | Edge |
|--------|--------|---------|--------|------|
| JPEG | ✅ | ✅ | ✅ | ✅ |
| PNG | ✅ | ✅ | ✅ | ✅ |
| WebP | ✅ 17+ | ✅ 65+ | ✅ 14+ | ✅ 18+ |
| AVIF | ✅ 85+ | ✅ 93+ | ✅ 16+ | ✅ 121+ |

---

## Migration Path

### Phase 1: PNG Support (Immediate)
- Add format configuration options
- Update endpoints to support `.png` extension
- PNG already supported in `DeepZoomGeneratorExtensions`
- Default: highest resolution level = PNG

### Phase 2: Client Format Negotiation
- Support format via URL extension
- Cache multiple formats per tile
- Add format to tile generation endpoint

### Phase 3: WebP Lossless Support (Future)
- Add ImageSharp or SkiaSharp dependency
- Implement `GetTileAsWebPStream`
- Better compression than PNG with same lossless guarantee

### Phase 4: AVIF Support (Future)
- Monitor browser adoption
- Evaluate encoding performance
- Best compression option when widely supported

---

## Files to Modify

### SingleSlideServer
- `ImageOption.cs` - Add TileFormatOptions
- `appsettings.json` - Add TileFormat configuration
- `Program.cs` - Update tile endpoint for format support
- `Services/TileGeneratorService.cs` - Format-aware generation and caching

### MultiSlideServer
- `ImagesOption.cs` - Add TileFormatOptions
- `appsettings.json` - Add TileFormat configuration  
- `Program.cs` - Update tile endpoint for format support
- `Services/TileGeneratorService.cs` - Format-aware generation and caching

### Core Library
- `OpenSlideSharp.BitmapExtensions/DeepZoomGeneratorExtensions.cs` - Add unified `GetTileAsStream` method

---

## Regulatory Documentation

When implementing for regulatory compliance, document:

1. **Format guarantee:** PNG uses DEFLATE compression which is mathematically lossless
2. **Verification:** Tile bytes can be decoded and compared bit-for-bit with source region
3. **Configuration audit trail:** Log which format was served for each tile request
4. **Level mapping:** Document which zoom levels are lossless vs lossy

---

## Summary

| Approach | Effort | Storage Impact | Compliance |
|----------|--------|----------------|------------|
| PNG highest level only | Low | +10-20% | ✅ Full at max zoom |
| PNG top 3 levels | Medium | +50-100% | ✅ Full at diagnostic levels |
| PNG all levels | Low | +300-400% | ✅ Complete |
| Client negotiation | High | Variable | ✅ Flexible |

**Recommended:** Start with **PNG for highest resolution level only** (Option A) with the configuration framework from Option B. This provides immediate regulatory compliance with minimal storage impact and can be expanded later.
