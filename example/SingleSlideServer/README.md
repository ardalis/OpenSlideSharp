# SingleSlideServer

A minimal ASP.NET Core web application that serves a single whole-slide image using [OpenSlide](http://openslide.org/) and displays it in the browser using [OpenSeadragon](https://openseadragon.github.io/).

## Prerequisites

- .NET 8.0 SDK or later
- A whole-slide image file supported by OpenSlide

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

The application expects an image file path to be configured in `appsettings.json`:

```json
{
  "Image": {
    "Path": "image.tiff"
  }
}
```

### Setting Up Your Image

1. **Option 1:** Place your whole-slide image in the project directory and name it `image.tiff`, or
2. **Option 2:** Update the `Image:Path` setting in `appsettings.json` to point to your image file (can be an absolute or relative path)

You can also override this setting using:

**Command line arguments:**
```bash
dotnet run --Image:Path=/path/to/your/slide.svs
```

**Environment variables:**
```bash
Image__Path=/path/to/your/slide.svs dotnet run
```

## Running the Application

```bash
cd example/SingleSlideServer
dotnet run
```

With a custom image path:

```bash
dotnet run --Image:Path=/path/to/your/slide.svs
```

Or from the solution root:

```bash
dotnet run --project example/SingleSlideServer/SingleSlideServer.csproj --Image:Path=/path/to/your/slide.svs
```

The application will start and listen on the configured URLs (typically `http://localhost:5000` or `https://localhost:5001`).

## How It Works

1. **ImageProvider** - Loads the whole-slide image using OpenSlideSharp and creates a `DeepZoomGenerator` for tile-based access
2. **HomeController** - Serves the Deep Zoom Image (DZI) XML descriptor at `/image.dzi`
3. **Tile Middleware** - Serves individual image tiles at `/image_files/{level}/{col}_{row}.jpeg`
4. **OpenSeadragon Viewer** - The frontend uses OpenSeadragon to display the zoomable image with smooth pan/zoom navigation

## Endpoints

| Endpoint | Description |
|----------|-------------|
| `/` | Main viewer page (index.html) |
| `/image.dzi` | Deep Zoom Image XML descriptor |
| `/image_files/{level}/{col}_{row}.jpeg` | Individual image tiles |

## Sample Images

You can download sample whole-slide images for testing from:

- [OpenSlide Test Data](https://openslide.org/demo/)
- [TCGA (The Cancer Genome Atlas)](https://portal.gdc.cancer.gov/) - requires registration

## Troubleshooting

### "The specified file is not a valid OpenSlide image"

Make sure you're using a supported whole-slide image format. Regular photographs or non-tiled TIFFs are not supported.

### Image not loading

- Verify the image path in `appsettings.json` is correct
- Check that the file exists and is readable
- Ensure the image format is supported by OpenSlide
