using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using OpenSlideSharp.BitmapExtensions;
using SingleSlideServer;
using SingleSlideServer.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure services
builder.Services.Configure<ImageOption>(builder.Configuration.GetSection("Image"));
builder.Services.AddSingleton<ImageProvider>();
builder.Services.AddSingleton<TileGeneratorService>();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

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

        var options = context.RequestServices.GetRequiredService<IOptions<ImageOption>>().Value;
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

        // Generate tile on-the-fly
        var provider = context.RequestServices.GetRequiredService<ImageProvider>();
        using var tileStream = provider.DeepZoomGenerator.GetTileAsJpegStream(result.level, result.col, result.row, out _);

        // Optionally save to disk for future requests
        if (options.EnableDiskCache)
        {
            var cachePath = Path.Combine(
                options.TileCachePath,
                result.level.ToString(),
                $"{result.col}_{result.row}.jpeg");

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

            // Read into memory so we can write to both file and response
            using var memoryStream = new MemoryStream();
            await tileStream.CopyToAsync(memoryStream);

            // Save to disk
            memoryStream.Position = 0;
            await using (var fileStream = File.Create(cachePath))
            {
                await memoryStream.CopyToAsync(fileStream);
            }

            // Send to response
            memoryStream.Position = 0;
            await memoryStream.CopyToAsync(response.Body);
        }
        else
        {
            await tileStream.CopyToAsync(response.Body);
        }
    });
});

// Tile generation API endpoints
app.MapPost("/api/tiles/generate", async (TileGeneratorService generator, bool? overwrite, CancellationToken ct) =>
{
    var result = await generator.GenerateAllTilesAsync(overwrite ?? false, cancellationToken: ct);
    return Results.Ok(new
    {
        status = "completed",
        tilesGenerated = result.TilesGenerated,
        tilesSkipped = result.TilesSkipped,
        duration = result.Duration.ToString(@"hh\:mm\:ss\.fff")
    });
});

app.MapGet("/api/tiles/stats", (TileGeneratorService generator) =>
{
    var (fileCount, totalSize) = generator.GetCacheStats();
    var totalTiles = generator.GetTotalTileCount();
    return Results.Ok(new
    {
        cachedTiles = fileCount,
        totalTiles = totalTiles,
        cachePercentage = totalTiles > 0 ? Math.Round((double)fileCount / totalTiles * 100, 2) : 0,
        cacheSizeBytes = totalSize,
        cacheSizeMB = Math.Round(totalSize / (1024.0 * 1024.0), 2)
    });
});

app.MapDelete("/api/tiles/cache", (TileGeneratorService generator) =>
{
    generator.ClearCache();
    return Results.Ok(new { status = "cache cleared" });
});

app.UseDefaultFiles();
app.UseStaticFiles();

// Deep Zoom Image descriptor
app.MapGet("/image.dzi", (ImageProvider provider) =>
{
    return Results.Content(provider.DeepZoomGenerator.GetDzi(), "application/xml");
});

app.Run();

// expression: /{level}/{col}_{row}.jpeg
static bool TryParseDeepZoom(string expression, out (int level, int col, int row, string format) result)
{
    if (expression.Length < 4 || expression[0] != '/')
    {
        result = default;
        return false;
    }

    // seg: {level}/{col}_{row}.jpeg
    StringSegment seg = new StringSegment(expression, 1, expression.Length - 1);
    int iPos = seg.IndexOf('/');
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

    result = (level: resultLevel, col: resultCol, row: resultRow, format: seg.ToString());
    return true;
}
