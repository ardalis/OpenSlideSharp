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

// Tile serving middleware with disk caching (handles /storage/{name}_files/{level}/{col}_{row}.{format})
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/storage", out var remaining))
    {
        await next();
        return;
    }

    if (!TryParseDeepZoom(remaining, out var result))
    {
        await next();
        return;
    }

    // Accept both jpeg and png format requests
    if (result.format != "jpeg" && result.format != "png")
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

    // Get the DeepZoomGenerator to determine level count for format decision
    var dz = provider.RetainDeepZoomGenerator(result.name, path);
    try
    {
        // Determine effective format based on configuration and level
        var effectiveFormat = provider.TileFormat.GetFormatForLevel(result.level, dz.LevelCount);
        
        response.ContentType = TileFormatOptions.GetContentType(effectiveFormat);
        var fileExtension = TileFormatOptions.GetFileExtension(effectiveFormat);

        // Check disk cache first
        if (provider.EnableDiskCache)
        {
            var cachedStream = await tileService.GetCachedTileAsync(
                result.name, result.level, result.col, result.row, effectiveFormat);

            if (cachedStream != null)
            {
                await using (cachedStream)
                {
                    await cachedStream.CopyToAsync(response.Body);
                }
                return;
            }
        }

        // Generate tile on-the-fly in the appropriate format
        MemoryStream tileStream = effectiveFormat == "png"
            ? dz.GetTileAsPngStream(result.level, result.col, result.row, out _)
            : dz.GetTileAsJpegStream(result.level, result.col, result.row, out _, provider.TileFormat.JpegQuality);

        using (tileStream)
        {
            if (provider.EnableDiskCache)
            {
                // Read into memory so we can write to both file and response
                using var memoryStream = new MemoryStream();
                await tileStream.CopyToAsync(memoryStream);

                // Save to disk
                memoryStream.Position = 0;
                await tileService.SaveTileToDiskAsync(
                    result.name, result.level, result.col, result.row, memoryStream, effectiveFormat);

                // Send to response
                memoryStream.Position = 0;
                await memoryStream.CopyToAsync(response.Body);
            }
            else
            {
                await tileStream.CopyToAsync(response.Body);
            }
        }
    }
    finally
    {
        dz.Release();
    }
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
            imageName, overwrite ?? false, ct: ct);

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
