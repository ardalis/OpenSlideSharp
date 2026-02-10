namespace MultiSlideServer
{
    public class ImagesOption
    {
        public ImageOptionItem[] Images { get; set; } = Array.Empty<ImageOptionItem>();
        public string TileCachePath { get; set; } = "./tile_cache";
        public bool EnableDiskCache { get; set; } = true;

        /// <summary>
        /// Tile format configuration for lossless support at high resolution levels.
        /// </summary>
        public TileFormatOptions TileFormat { get; set; } = new();
    }

    public class ImageOptionItem
    {
        public string Name { get; set; } = null!;
        public string Path { get; set; } = null!;
    }
}
