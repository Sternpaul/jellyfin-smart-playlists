using SkiaSharp;
using System.Text;

namespace Jellyfin.Plugin.AIRecommender.Services.Playlists;

public sealed class PlaylistArtworkFontUnavailableException : InvalidOperationException
{
    public PlaylistArtworkFontUnavailableException(string message)
        : base(message)
    {
    }
}

public static class PlaylistArtworkComposer
{
    private const int MaximumSourcePixels = 40_000_000;
    private const int MaximumSourceDimension = 10_000;
    private const int MaximumSourceBytes = 50 * 1024 * 1024;
    private const int MaximumTitleRunes = 96;

    public static bool IsSourceSizeAllowed(int width, int height)
        => width > 0
            && height > 0
            && width <= MaximumSourceDimension
            && height <= MaximumSourceDimension
            && (long)width * height <= MaximumSourcePixels;

    public static byte[] Compose(byte[] sourceBytes, string title, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        if (sourceBytes.Length == 0 || sourceBytes.Length > MaximumSourceBytes)
            throw new InvalidDataException("The source artwork is empty or exceeds the decode limit.");
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("A playlist title is required.", nameof(title));
        if (width <= 0 || height <= 0 || width > 2000 || height > 2000)
            throw new ArgumentOutOfRangeException(nameof(width), "Output artwork dimensions must be between 1 and 2000 pixels.");

        using var encoded = SKData.CreateCopy(sourceBytes);
        using var codec = SKCodec.Create(encoded)
            ?? throw new InvalidDataException("The source artwork is not a supported image.");
        if (!IsSourceSizeAllowed(codec.Info.Width, codec.Info.Height))
            throw new InvalidDataException($"Source artwork dimensions {codec.Info.Width}x{codec.Info.Height} exceed the decode limit.");

        using var source = SKBitmap.Decode(encoded)
            ?? throw new InvalidDataException("The source artwork could not be decoded.");
        using var output = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgb888x, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Black);

        var sourceRect = CoverCrop(source.Width, source.Height, width, height);
        var destinationRect = new SKRect(0, 0, width, height);
        using (var sourceImage = SKImage.FromBitmap(source))
        using (var imagePaint = new SKPaint { IsAntialias = true })
            canvas.DrawImage(
                sourceImage,
                sourceRect,
                destinationRect,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
                imagePaint);

        using (var scrim = new SKPaint { Color = new SKColor(0, 0, 0, 142) })
            canvas.DrawRect(destinationRect, scrim);

        using (var vignette = new SKPaint())
        {
            vignette.Shader = SKShader.CreateRadialGradient(
                new SKPoint(width / 2f, height / 2f),
                Math.Max(width, height) * 0.72f,
                new[] { new SKColor(0, 0, 0, 20), new SKColor(0, 0, 0, 150) },
                new[] { 0.30f, 1f },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(destinationRect, vignette);
        }

        DrawTitle(canvas, NormalizeTitle(title), width, height);
        canvas.Flush();

        using var image = SKImage.FromBitmap(output);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("SkiaSharp failed to encode the playlist artwork PNG.");
        return png.ToArray();
    }

    private static SKRect CoverCrop(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        var sourceRatio = sourceWidth / (float)sourceHeight;
        var outputRatio = outputWidth / (float)outputHeight;
        if (sourceRatio > outputRatio)
        {
            var cropWidth = sourceHeight * outputRatio;
            var left = (sourceWidth - cropWidth) / 2f;
            return new SKRect(left, 0, left + cropWidth, sourceHeight);
        }

        var cropHeight = sourceWidth / outputRatio;
        var top = (sourceHeight - cropHeight) / 2f;
        return new SKRect(0, top, sourceWidth, top + cropHeight);
    }

    private static void DrawTitle(SKCanvas canvas, string title, int width, int height)
    {
        using var typeface = ResolveSansTypeface();
        var maximumTextWidth = width * 0.78f;
        var maximumSize = Math.Min(width, height) * 0.105f;
        var minimumSize = Math.Min(width, height) * 0.048f;
        var textSize = maximumSize;
        List<string> lines;

        using var measurePaint = new SKPaint { IsAntialias = true };
        using var font = new SKFont(typeface, textSize);
        while (true)
        {
            font.Size = textSize;
            lines = Wrap(title, maximumTextWidth, font, measurePaint);
            if (lines.Count <= 3 || textSize <= minimumSize)
                break;
            textSize = Math.Max(minimumSize, textSize - 4f);
        }

        while (lines.Any(line => font.MeasureText(line, measurePaint) > maximumTextWidth) && textSize > minimumSize)
        {
            textSize = Math.Max(minimumSize, textSize - 3f);
            font.Size = textSize;
            lines = Wrap(title, maximumTextWidth, font, measurePaint);
        }

        if (lines.Count > 3)
        {
            lines = lines.Take(3).ToList();
            lines[2] = FitWithEllipsis(lines[2], maximumTextWidth, font, measurePaint);
        }

        var lineHeight = textSize * 1.13f;
        var totalHeight = lines.Count * lineHeight;
        var firstBaseline = (height - totalHeight) / 2f + textSize;
        using var shadow = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 210)
        };
        using var text = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White
        };

        for (var index = 0; index < lines.Count; index++)
        {
            var baseline = firstBaseline + index * lineHeight;
            canvas.DrawText(lines[index], width / 2f + 4f, baseline + 5f, SKTextAlign.Center, font, shadow);
            canvas.DrawText(lines[index], width / 2f, baseline, SKTextAlign.Center, font, text);
        }

        var accentTop = firstBaseline + (lines.Count - 1) * lineHeight + textSize * 0.42f;
        var accentWidth = Math.Min(width * 0.28f, 360f);
        var accentHeight = Math.Max(6f, height * 0.009f);
        using var accent = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint((width - accentWidth) / 2f, accentTop),
                new SKPoint((width + accentWidth) / 2f, accentTop),
                new[] { new SKColor(0, 164, 220), new SKColor(170, 92, 195) },
                null,
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRoundRect(
            new SKRect((width - accentWidth) / 2f, accentTop, (width + accentWidth) / 2f, accentTop + accentHeight),
            accentHeight / 2f,
            accentHeight / 2f,
            accent);
    }

    private static List<string> Wrap(string title, float maximumWidth, SKFont font, SKPaint paint)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in words)
        {
            if (font.MeasureText(word, paint) > maximumWidth)
            {
                if (current.Length > 0)
                {
                    lines.Add(current);
                    current = string.Empty;
                }

                var segment = new StringBuilder();
                foreach (var rune in word.EnumerateRunes())
                {
                    var candidateSegment = segment.ToString() + rune;
                    if (segment.Length > 0 && font.MeasureText(candidateSegment, paint) > maximumWidth)
                    {
                        lines.Add(segment.ToString());
                        segment.Clear();
                    }

                    segment.Append(rune);
                }

                current = segment.ToString();
                continue;
            }

            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && font.MeasureText(candidate, paint) > maximumWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0)
            lines.Add(current);
        return lines;
    }

    private static string FitWithEllipsis(string value, float maximumWidth, SKFont font, SKPaint paint)
    {
        const string ellipsis = "…";
        var runes = value.EnumerateRunes().ToList();
        while (runes.Count > 0)
        {
            var candidate = string.Concat(runes.Select(rune => rune.ToString())) + ellipsis;
            if (font.MeasureText(candidate, paint) <= maximumWidth)
                return candidate;
            runes.RemoveAt(runes.Count - 1);
        }

        return ellipsis;
    }

    private static string NormalizeTitle(string title)
    {
        var normalized = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var runes = normalized.EnumerateRunes().Take(MaximumTitleRunes).ToList();
        var bounded = string.Concat(runes.Select(rune => rune.ToString()));
        return normalized.EnumerateRunes().Skip(MaximumTitleRunes).Any() ? bounded + "…" : bounded;
    }

    private static SKTypeface ResolveSansTypeface()
    {
        foreach (var family in new[] { "DejaVu Sans", "Liberation Sans", "Noto Sans" })
        {
            var candidate = SKTypeface.FromFamilyName(family, SKFontStyle.Bold);
            if (candidate != null
                && !candidate.FamilyName.Contains("Serif", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            candidate?.Dispose();
        }

        throw new PlaylistArtworkFontUnavailableException(
            "No verified bold sans-serif typeface is available for playlist artwork composition.");
    }
}
