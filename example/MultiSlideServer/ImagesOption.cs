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
