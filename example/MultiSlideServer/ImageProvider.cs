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
    public TileFormatOptions TileFormat => _options.TileFormat;

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
        // Retry loop to handle race condition where cache entry is evicted
        // between TryGet and Retain calls
        while (true)
        {
            if (_cache.TryGet(name, out var dz))
            {
                try
                {
                    dz.Retain();
                    return dz;
                }
                catch (ObjectDisposedException)
                {
                    // Entry was evicted between TryGet and Retain, retry
                    continue;
                }
            }

            dz = new RetainableDeepZoomGenerator(OpenSlideImage.Open(path));
            if (_cache.TrySet(name, dz))
            {
                dz.Retain();
                return dz;
            }

            // Another thread added an entry, dispose ours and retry
            dz.Dispose();
        }
    }
}
