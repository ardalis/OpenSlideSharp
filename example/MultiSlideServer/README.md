# MultiSlideServer

A multi-image ASP.NET Core web application that serves multiple whole-slide images using [OpenSlide](http://openslide.org/) and displays them in the browser using [OpenSeadragon](https://openseadragon.github.io/).

## Prerequisites

- .NET 8.0 SDK or later
- One or more whole-slide image files supported by OpenSlide

## Supported Image Formats

OpenSlide supports several whole-slide image formats, including:

- **Aperio** (.svs, .tif)
- **Hamamatsu** (.vms, .vmu, .ndpi)
- **Leica** (.scn)
- **MIRAX** (.mrxs)
- **Philips** (.tiff)
- **Sakura** (.svslide)
- **Trestle** (.tif)
- **Ventana** (.bif, .tif)
- **Generic tiled TIFF** (.tif, .tiff)

> **Note:** Not all TIFF files are supported—only tiled TIFFs commonly used in digital pathology. Standard photographs saved as TIFF will not work.

## Configuration

The application expects image configurations in `appsettings.json`:

```json
{
  "TileCachePath": "./tile_cache",
  "EnableDiskCache": true,
  "TileFormat": {
    "Default": "jpeg",
    "LosslessFormat": "png",
    "LosslessLevelCount": 1,
    "JpegQuality": 90
  },
  "Images": [
    {
      "Name": "slide1",
      "Path": "/path/to/slide1.svs"
    },
    {
      "Name": "slide2",
      "Path": "/path/to/slide2.ndpi"
    }
  ]
}
```

### Configuration Options

| Setting | Description | Default |
|---------|-------------|---------|
| `TileCachePath` | Directory for cached tile images | `./tile_cache` |
| `EnableDiskCache` | Enable/disable disk caching of tiles | `true` |
| `TileFormat` | Tile format configuration (see below) | See below |
| `Images` | Array of image configurations | `[]` |

### Image Entry Configuration

| Setting | Description |
|---------|-------------|
| `Name` | Unique identifier for the image (used in URLs) |
| `Path` | Path to the whole-slide image file |

### Tile Format Configuration (Lossless Support)

For regulatory compliance in medical/diagnostic imaging, the server supports serving lossless PNG tiles at high resolution levels while using efficient JPEG for lower zoom levels.

| Setting | Description | Default |
|---------|-------------|---------|
| `TileFormat:Default` | Default format for tiles: "jpeg" or "png" | `jpeg` |
| `TileFormat:LosslessFormat` | Format for high-resolution tiles: "png" for lossless | `png` |
| `TileFormat:LosslessLevelCount` | Number of highest-resolution levels to serve as lossless (0=none, 1=highest only, -1=all) | `1` |
| `TileFormat:JpegQuality` | JPEG quality (1-100) when using JPEG format | `90` |

**Examples:**
- `LosslessLevelCount: 1` - Only the highest resolution level uses PNG (default)
- `LosslessLevelCount: 3` - Top 3 zoom levels use PNG
- `LosslessLevelCount: -1` - All levels use PNG (full lossless)
- `LosslessLevelCount: 0` - All levels use JPEG (no lossless)

> **Note:** JPEG is always lossy, even at quality 100. PNG uses lossless DEFLATE compression, ensuring bit-perfect fidelity to the original slide data. The URL extension always shows `.jpeg` for OpenSeadragon compatibility, but the server returns the correct `Content-Type` header and browsers decode the actual format correctly.

## Running the Application

```bash
cd example/MultiSlideServer
dotnet run
```

The application will start and listen on the configured URLs (typically `http://localhost:5000` or `https://localhost:5001`).

## How It Works

1. **ImageProvider** - Manages multiple whole-slide images using OpenSlideSharp with a `DeepZoomGeneratorCache` for efficient memory management
2. **DeepZoomGeneratorCache** - Caches `DeepZoomGenerator` instances with sliding expiration
3. **Tile Middleware** - Serves individual image tiles at `/storage/{name}_files/{level}/{col}_{row}.jpeg`
4. **OpenSeadragon Viewer** - The frontend uses OpenSeadragon to display zoomable images with smooth pan/zoom navigation

## Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/` | GET | Home page listing all available images |
| `/slide/{name}.html` | GET | Viewer page for a specific image |
| `/storage/{name}.dzi` | GET | Deep Zoom Image XML descriptor for an image |
| `/storage/{name}_files/{level}/{col}_{row}.jpeg` | GET | Individual image tiles (format determined by level) |
| `/api/tiles/{imageName}/generate` | POST | Pre-generate all tiles for an image |
| `/api/tiles/{imageName}/stats` | GET | Get cache statistics for an image |
| `/api/tiles/{imageName}/cache` | DELETE | Clear the tile cache for an image |
| `/api/tiles/generate-all` | POST | Pre-generate tiles for all images |
| `/api/tiles/stats` | GET | Get cache statistics for all images |
| `/api/tiles/cache` | DELETE | Clear all tile caches |

## Tile Caching

When `EnableDiskCache` is `true`:
- Tiles are checked on disk first before generating
- Generated tiles are automatically saved to disk for future requests
- Each image has its own cache subdirectory: `{TileCachePath}/{imageName}/`
- Use the API endpoints to pre-generate tiles upfront

### Pre-generating Tiles

To pre-generate all tiles for a specific image:

```bash
curl -X POST http://localhost:5000/api/tiles/slide1/generate
```

To pre-generate tiles for all images:

```bash
curl -X POST http://localhost:5000/api/tiles/generate-all
```

With overwrite option to regenerate existing tiles:

```bash
curl -X POST "http://localhost:5000/api/tiles/slide1/generate?overwrite=true"
```

### Cache Statistics

For a specific image:
```bash
curl http://localhost:5000/api/tiles/slide1/stats
```

For all images:
```bash
curl http://localhost:5000/api/tiles/stats
```

### Clearing the Cache

For a specific image:
```bash
curl -X DELETE http://localhost:5000/api/tiles/slide1/cache
```

For all images:
```bash
curl -X DELETE http://localhost:5000/api/tiles/cache
```

## Sample Images

You can download sample whole-slide images for testing from:

- [OpenSlide Test Data](https://openslide.org/demo/)
- [TCGA (The Cancer Genome Atlas)](https://portal.gdc.cancer.gov/) - requires registration

## Troubleshooting

### "Image not found"

Make sure the image name in the URL matches the `Name` property in the configuration.

### "The specified file is not a valid OpenSlide image"

Make sure you're using a supported whole-slide image format. Regular photographs or non-tiled TIFFs are not supported.

### Image not loading

- Verify the image path in `appsettings.json` is correct
- Check that the file exists and is readable
- Ensure the image format is supported by OpenSlide
