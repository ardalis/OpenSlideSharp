using Microsoft.Extensions.Primitives;
using OpenSlideSharp.BitmapExtensions;
using SingleSlideServer;

var builder = WebApplication.CreateBuilder(args);

// Configure services
builder.Services.Configure<ImageOption>(builder.Configuration.GetSection("Image"));
builder.Services.AddSingleton<ImageProvider>();
builder.Services.AddControllers();

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

        var provider = context.RequestServices.GetService<ImageProvider>()!;
        var response = context.Response;
        response.ContentType = "image/jpeg";
        await provider.DeepZoomGenerator.GetTileAsJpegStream(result.level, result.col, result.row, out var tmp).CopyToAsync(response.Body);
    });
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.MapControllers();

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
