using OpenSlideSharp;
var path = @"test\OpenSlideSharp.Tests\Assets\CMU-1.svs";
Console.WriteLine($"Testing OpenSlideSharp with: {path}");
using var slide = OpenSlideImage.Open(path);
Console.WriteLine($"  Vendor: {slide.GetProperty<string>("openslide.vendor")}");
Console.WriteLine($"  Dimensions: {slide.Dimension.Width} x {slide.Dimension.Height}");
Console.WriteLine($"  Level count: {slide.LevelCount}");
Console.WriteLine("SUCCESS - OpenSlideSharp is working!");
