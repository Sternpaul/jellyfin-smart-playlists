using Jellyfin.Plugin.AIRecommender.Services.Playlists;
using SkiaSharp;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public sealed class PlaylistArtworkComposerTests
{
    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1600, 900)]
    public void Composite_is_deterministic_opaque_png_at_requested_dimensions(int width, int height)
    {
        var source = CreateSourcePng(1920, 1080, new SKColor(235, 190, 70));

        var first = PlaylistArtworkComposer.Compose(source, "More Like Your Favorites", width, height);
        var second = PlaylistArtworkComposer.Compose(source, "More Like Your Favorites", width, height);

        Assert.Equal(first, second);
        using var bitmap = SKBitmap.Decode(first);
        Assert.NotNull(bitmap);
        Assert.Equal(width, bitmap.Width);
        Assert.Equal(height, bitmap.Height);
        Assert.Equal(SKAlphaType.Opaque, bitmap.AlphaType);
    }

    [Fact]
    public void Composite_darkens_bright_source_and_draws_visible_white_title()
    {
        var source = CreateSourcePng(1600, 900, SKColors.White);
        var output = PlaylistArtworkComposer.Compose(source, "For You", 1600, 900);
        using var bitmap = SKBitmap.Decode(output);

        var center = bitmap.GetPixel(800, 450);
        Assert.True(center.Red < 190 && center.Green < 190 && center.Blue < 190);

        var nearWhitePixels = 0;
        for (var y = 250; y < 650; y += 4)
        for (var x = 250; x < 1350; x += 4)
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.Red > 235 && pixel.Green > 235 && pixel.Blue > 235)
                nearWhitePixels++;
        }
        Assert.True(nearWhitePixels > 40, $"Expected visible white title pixels, found {nearWhitePixels}.");
    }

    [Fact]
    public void Composite_rejects_invalid_or_excessive_source_dimensions()
    {
        Assert.Throws<InvalidDataException>(() => PlaylistArtworkComposer.Compose("not an image"u8.ToArray(), "For You", 1000, 1000));
        Assert.False(PlaylistArtworkComposer.IsSourceSizeAllowed(12000, 12000));
        Assert.True(PlaylistArtworkComposer.IsSourceSizeAllowed(3840, 2160));
    }

    [Fact]
    public void Composite_bounds_extreme_unicode_and_unbroken_titles()
    {
        var source = CreateSourcePng(1600, 900, new SKColor(40, 80, 120));
        var title = string.Concat(Enumerable.Repeat("超長タイトルWithoutAnySpaces", 500));

        var first = PlaylistArtworkComposer.Compose(source, title, 1600, 900);
        var second = PlaylistArtworkComposer.Compose(source, title, 1600, 900);

        Assert.Equal(first, second);
        using var bitmap = SKBitmap.Decode(first);
        Assert.Equal(1600, bitmap.Width);
        Assert.Equal(900, bitmap.Height);
    }

    private static byte[] CreateSourcePng(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
