namespace SingleSlideServer
{
    public class ImageOption
    {
        public string Path { get; set; } = null!;
        public string TileCachePath { get; set; } = "./tile_cache";
        public bool EnableDiskCache { get; set; } = true;
    }
}
