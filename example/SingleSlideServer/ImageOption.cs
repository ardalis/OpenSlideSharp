namespace SingleSlideServer
{
    public class ImageOption
    {
        public string Path { get; set; } = null!;
        public string TileCachePath { get; set; } = "./tile_cache";
        public bool EnableDiskCache { get; set; } = true;

        /// <summary>
        /// Tile format configuration for lossless support at high resolution levels.
        /// </summary>
        public TileFormatOptions TileFormat { get; set; } = new();
    }
}
