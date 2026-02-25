using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace VolMon.Hardware.Beacn.Mix.Display;

/// <summary>
/// Renders a DisplayLayout template to a JPEG byte array by walking the slot tree,
/// resolving data bindings, and drawing each element with SkiaSharp.
///
/// Supports partial updates by comparing the new frame against a cached previous frame
/// and returning only the changed region as JPEG data.
/// </summary>
internal static class TemplateRenderer
{
    private static readonly SKColor DefaultText = new(0xFF, 0xFF, 0xFF);
    private static readonly SKColor DefaultTrack = new(0x3A, 0x3A, 0x3A);

    /// <summary>
    /// Cache loaded images by absolute path so we don't hit disk every frame.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SKImage?> ImageCache = new();

    /// <summary>
    /// Cache loaded typefaces by resolved path or family name so we don't hit disk every frame.
    /// Keys are either absolute file paths (for custom font files) or family names (for system fonts).
    /// </summary>
    private static readonly ConcurrentDictionary<string, SKTypeface?> TypefaceCache = new();

    /// <summary>
    /// Result of a render operation, potentially containing a partial update.
    /// </summary>
    public readonly struct RenderResult
    {
        /// <summary>JPEG data for the updated region (or full frame).</summary>
        public byte[] JpegData { get; init; }

        /// <summary>X position to place the JPEG on the device display.</summary>
        public int X { get; init; }

        /// <summary>Y position to place the JPEG on the device display.</summary>
        public int Y { get; init; }

        /// <summary>Whether this is a partial update (true) or a full frame (false).</summary>
        public bool IsPartial { get; init; }
    }

    /// <summary>
    /// Render a full frame without any diffing (used for initial render or layout changes).
    /// </summary>
    public static byte[] Render(DisplayLayout layout, ReadOnlySpan<GroupDisplayState> groups)
    {
        using var surface = SKSurface.Create(new SKImageInfo(layout.Width, layout.Height));
        var canvas = surface.Canvas;

        RenderToCanvas(canvas, layout, groups);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(layout.JpegQuality, 1, 100));
        return data.ToArray();
    }

    /// <summary>
    /// Render with dirty-region detection. Compares the new frame against a cached previous
    /// frame and returns only the changed region as JPEG data when possible.
    /// </summary>
    /// <param name="layout">The display layout to render.</param>
    /// <param name="groups">Current group display states.</param>
    /// <param name="previousPixels">
    /// Cached pixel data from the previous frame (BGRA, layout.Width * layout.Height * 4 bytes).
    /// Pass null to force a full render. Will be updated in-place with the new frame's pixels.
    /// </param>
    /// <returns>
    /// A RenderResult with JPEG data and positioning, or null if the frame is identical
    /// to the previous one.
    /// </returns>
    public static RenderResult? RenderWithDiff(
        DisplayLayout layout,
        ReadOnlySpan<GroupDisplayState> groups,
        ref byte[]? previousPixels)
    {
        var width = layout.Width;
        var height = layout.Height;
        var pixelByteCount = width * height * 4; // BGRA

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;

        RenderToCanvas(canvas, layout, groups);

        // Read pixel data from the rendered surface
        using var pixmap = surface.PeekPixels();
        var currentPixels = new byte[pixelByteCount];
        Marshal.Copy(pixmap.GetPixels(), currentPixels, 0, pixelByteCount);

        // First render or layout change — no previous frame to compare against
        if (previousPixels is null || previousPixels.Length != pixelByteCount)
        {
            previousPixels = currentPixels;

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(layout.JpegQuality, 1, 100));
            return new RenderResult
            {
                JpegData = data.ToArray(),
                X = 0,
                Y = 0,
                IsPartial = false
            };
        }

        // Compare pixels to find the dirty bounding rectangle
        var dirty = FindDirtyRect(previousPixels, currentPixels, width, height);

        if (dirty is null)
        {
            // Frames are identical — nothing to send
            return null;
        }

        var (dirtyX, dirtyY, dirtyW, dirtyH) = dirty.Value;

        // Update the cached pixels for next comparison
        Array.Copy(currentPixels, previousPixels, pixelByteCount);

        // If the dirty region is more than 60% of the total area, just send a full frame
        // (partial JPEG overhead + decode latency makes it not worth it for large regions)
        var dirtyArea = (long)dirtyW * dirtyH;
        var totalArea = (long)width * height;
        if (dirtyArea * 100 / totalArea > 60)
        {
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(layout.JpegQuality, 1, 100));
            return new RenderResult
            {
                JpegData = data.ToArray(),
                X = 0,
                Y = 0,
                IsPartial = false
            };
        }

        // Extract and encode only the dirty region
        var regionJpeg = ExtractRegionJpeg(surface, dirtyX, dirtyY, dirtyW, dirtyH, layout.JpegQuality);

        return new RenderResult
        {
            JpegData = regionJpeg,
            X = dirtyX,
            Y = dirtyY,
            IsPartial = true
        };
    }

    /// <summary>
    /// Find the minimal bounding rectangle of pixels that differ between two frames.
    /// Returns null if the frames are identical.
    /// </summary>
    private static (int X, int Y, int W, int H)? FindDirtyRect(
        byte[] previous, byte[] current, int width, int height)
    {
        var stride = width * 4;
        int minX = width, minY = height, maxX = -1, maxY = -1;

        // Compare as 32-bit integers for speed (each pixel is 4 bytes BGRA)
        var prevSpan = MemoryMarshal.Cast<byte, uint>(previous.AsSpan());
        var currSpan = MemoryMarshal.Cast<byte, uint>(current.AsSpan());

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * width;
            for (var x = 0; x < width; x++)
            {
                if (prevSpan[rowOffset + x] != currSpan[rowOffset + x])
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (maxX < 0)
            return null; // Identical

        var dirtyW = maxX - minX + 1;
        var dirtyH = maxY - minY + 1;

        return (minX, minY, dirtyW, dirtyH);
    }

    /// <summary>
    /// Extract a rectangular region from a surface and encode it as JPEG.
    /// </summary>
    private static byte[] ExtractRegionJpeg(SKSurface sourceSurface, int x, int y, int w, int h, int quality)
    {
        using var regionSurface = SKSurface.Create(new SKImageInfo(w, h));
        var canvas = regionSurface.Canvas;

        // Draw the source region onto the smaller surface
        using var sourceImage = sourceSurface.Snapshot();
        var srcRect = new SKRect(x, y, x + w, y + h);
        var dstRect = new SKRect(0, 0, w, h);
        canvas.DrawImage(sourceImage, srcRect, dstRect);

        using var image = regionSurface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
        return data.ToArray();
    }

    /// <summary>
    /// Render the layout to a canvas (shared logic between full and diff renders).
    /// </summary>
    private static void RenderToCanvas(SKCanvas canvas, DisplayLayout layout, ReadOnlySpan<GroupDisplayState> groups)
    {
        // Background color
        var bg = SKColor.TryParse(layout.Background, out var bgColor) ? bgColor : new SKColor(0x2B, 0x2B, 0x2B);
        canvas.Clear(bg);

        // Background image (drawn after color, before slots)
        if (!string.IsNullOrEmpty(layout.BackgroundImage))
        {
            var bgImage = LoadImage(layout.BackgroundImage);
            if (bgImage is not null)
            {
                var dest = new SKRect(0, 0, layout.Width, layout.Height);
                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawImage(bgImage, dest, SKSamplingOptions.Default, paint);
            }
        }

        // Draw each slot
        var defaultFontFamily = layout.FontFamily;
        foreach (var slot in layout.Slots)
        {
            DrawSlot(canvas, slot, groups, defaultFontFamily);
        }
    }

    private static void DrawSlot(SKCanvas canvas, DisplaySlot slot, ReadOnlySpan<GroupDisplayState> groups, string? defaultFontFamily)
    {
        // Handle repeat
        if (slot.Repeat is not null)
        {
            var (start, end) = ParseRange(slot.Repeat);
            var offsetX = float.TryParse(slot.RepeatOffsetX, out var ox) ? ox : 0;
            var offsetY = float.TryParse(slot.RepeatOffsetY, out var oy) ? oy : 0;

            for (var i = start; i <= end && i < groups.Length; i++)
            {
                var iterOffset = (i - start);
                var tx = iterOffset * offsetX;
                var ty = iterOffset * offsetY;

                canvas.Save();
                canvas.Translate(tx, ty);

                // Draw this slot's own visuals (if it has a type other than just a container)
                if (slot.Type != 0 || slot.Children is null)
                    DrawSingleSlot(canvas, slot, groups[i], i, defaultFontFamily);

                // Draw children
                if (slot.Children is not null)
                {
                    foreach (var child in slot.Children)
                        DrawSingleSlot(canvas, child, groups[i], i, defaultFontFamily);
                }

                canvas.Restore();
            }
            return;
        }

        // Non-repeating: draw with group 0 context (or empty)
        var g = groups.Length > 0 ? groups[0] : default;
        DrawSingleSlot(canvas, slot, g, 0, defaultFontFamily);
    }

    private static void DrawSingleSlot(SKCanvas canvas, DisplaySlot slot, in GroupDisplayState group, int groupIndex, string? defaultFontFamily)
    {
        var x = BindingResolver.ResolveFloat(slot.X, group, groupIndex);
        var y = BindingResolver.ResolveFloat(slot.Y, group, groupIndex);
        var w = BindingResolver.ResolveFloat(slot.W, group, groupIndex);
        var h = BindingResolver.ResolveFloat(slot.H, group, groupIndex);

        switch (slot.Type)
        {
            case SlotType.Rect:
                DrawRect(canvas, slot, group, groupIndex, x, y, w, h);
                break;
            case SlotType.Text:
                DrawText(canvas, slot, group, groupIndex, x, y, w, defaultFontFamily);
                break;
            case SlotType.Bar:
                DrawBar(canvas, slot, group, groupIndex, x, y, w, h);
                break;
            case SlotType.Checkbox:
                DrawCheckbox(canvas, slot, group, groupIndex, x, y, defaultFontFamily);
                break;
            case SlotType.Line:
                DrawLine(canvas, slot, group, groupIndex, x, y, w, h);
                break;
            case SlotType.Arc:
                DrawArc(canvas, slot, group, groupIndex, x, y, w, h);
                break;
            case SlotType.Image:
                DrawImage(canvas, slot, group, groupIndex, x, y, w, h);
                break;
        }
    }

    // ── Slot renderers ──────────────────────────────────────────────

    private static void DrawRect(SKCanvas canvas, DisplaySlot slot,
        in GroupDisplayState group, int groupIndex,
        float x, float y, float w, float h)
    {
        var fill = BindingResolver.ResolveColor(slot.Fill ?? slot.Color, group, groupIndex, SKColors.Transparent);
        fill = BindingResolver.WithOpacity(fill, slot.Opacity);

        if (fill.Alpha > 0)
        {
            using var paint = new SKPaint { Color = fill, IsAntialias = true };
            var rect = new SKRect(x, y, x + w, y + h);
            if (slot.Radius > 0)
                canvas.DrawRoundRect(rect, slot.Radius, slot.Radius, paint);
            else
                canvas.DrawRect(rect, paint);
        }
    }

    private static void DrawText(SKCanvas canvas, DisplaySlot slot,
        in GroupDisplayState group, int groupIndex,
        float x, float y, float w, string? defaultFontFamily)
    {
        var text = BindingResolver.ResolveString(slot.Text, group, groupIndex);
        var secondaryText = BindingResolver.ResolveString(slot.SecondaryText, group, groupIndex);
        if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(secondaryText)) return;

        var color = BindingResolver.ResolveColor(slot.Color, group, groupIndex, DefaultText);
        color = BindingResolver.WithOpacity(color, slot.Opacity);

        var fontStyle = slot.FontWeight == FontWeight.Bold ? SKFontStyle.Bold : SKFontStyle.Normal;
        var typeface = ResolveTypeface(slot.FontFamily, defaultFontFamily, fontStyle);
        using var font = new SKFont(typeface, slot.FontSize);
        using var paint = new SKPaint { Color = color, IsAntialias = true };

        var textAlign = slot.Align switch
        {
            HAlign.Left => SKTextAlign.Left,
            HAlign.Right => SKTextAlign.Right,
            _ => SKTextAlign.Center
        };

        // For center/right alignment with a width, adjust x
        var drawX = textAlign switch
        {
            SKTextAlign.Center => x + w / 2,
            SKTextAlign.Right => x + w,
            _ => x
        };

        // If the text contains commas (e.g. member lists) or the text is wider than
        // the slot width, wrap it onto multiple lines. When w > 0 the slot has a
        // defined width we can clip/wrap to.
        var maxWidth = w > 0 ? w : float.MaxValue;

        var lineHeight = font.Size * 1.3f;
        var drawY = y + font.Size;

        var truncate = slot.Overflow == TextOverflow.Truncate;

        // Optional separator line between entries
        var hasSeparator = !string.IsNullOrEmpty(slot.SeparatorColor);
        SKPaint? separatorPaint = null;
        if (hasSeparator)
        {
            var sepColor = SKColor.TryParse(slot.SeparatorColor, out var sc) ? sc : new SKColor(0x33, 0x33, 0x33);
            separatorPaint = new SKPaint { Color = sepColor, IsAntialias = true, StrokeWidth = 1 };
        }

        var lineIndex = 0; // tracks total lines rendered (for separator logic)

        // Render primary text
        if (!string.IsNullOrEmpty(text))
        {
            var primaryLines = truncate
                ? TruncateLines(text, font, maxWidth)
                : WrapText(text, font, maxWidth);
            foreach (var line in primaryLines)
            {
                if (hasSeparator && lineIndex > 0)
                {
                    var sepY = drawY - lineHeight + (lineHeight - font.Size) / 2;
                    canvas.DrawLine(x, sepY, x + w, sepY, separatorPaint!);
                }
                canvas.DrawText(line, drawX, drawY, textAlign, font, paint);
                drawY += lineHeight;
                lineIndex++;
            }
        }

        // Render secondary text (if any) immediately after, in secondary color
        if (!string.IsNullOrEmpty(secondaryText))
        {
            var secondaryColor = BindingResolver.ResolveColor(
                slot.SecondaryColor, group, groupIndex, color);
            secondaryColor = BindingResolver.WithOpacity(secondaryColor, slot.Opacity);
            paint.Color = secondaryColor;

            var secondaryLines = truncate
                ? TruncateLines(secondaryText, font, maxWidth)
                : WrapText(secondaryText, font, maxWidth);
            foreach (var line in secondaryLines)
            {
                if (hasSeparator && lineIndex > 0)
                {
                    var sepY = drawY - lineHeight + (lineHeight - font.Size) / 2;
                    canvas.DrawLine(x, sepY, x + w, sepY, separatorPaint!);
                }
                canvas.DrawText(line, drawX, drawY, textAlign, font, paint);
                drawY += lineHeight;
                lineIndex++;
            }
        }

        separatorPaint?.Dispose();
    }

    /// <summary>
    /// Wrap text into lines that fit within maxWidth pixels.
    /// Explicit newlines (\n) always force a line break. Within each logical line,
    /// text is word-wrapped to fit within maxWidth, and individual words that still
    /// exceed the width are truncated with ellipsis.
    /// </summary>
    private static List<string> WrapText(string text, SKFont font, float maxWidth)
    {
        // Fast path: no wrapping needed (single line, fits)
        if (!text.Contains('\n') && font.MeasureText(text) <= maxWidth)
            return [text];

        var result = new List<string>();

        // Split on explicit newlines first — each is a separate logical line
        var logicalLines = text.Split('\n');

        foreach (var logicalLine in logicalLines)
        {
            var trimmed = logicalLine.Trim();
            if (trimmed.Length == 0)
            {
                result.Add("");
                continue;
            }

            // If the logical line fits, add it directly
            if (font.MeasureText(trimmed) <= maxWidth)
            {
                result.Add(trimmed);
                continue;
            }

            // Word-wrap this logical line
            var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var currentLine = "";

            foreach (var word in words)
            {
                var candidate = currentLine.Length == 0
                    ? word
                    : currentLine + " " + word;

                if (font.MeasureText(candidate) <= maxWidth)
                {
                    currentLine = candidate;
                }
                else
                {
                    if (currentLine.Length > 0)
                    {
                        result.Add(currentLine);
                        currentLine = word;
                    }
                    else
                    {
                        // Single word exceeds width — truncate with ellipsis
                        currentLine = TruncateWithEllipsis(word, font, maxWidth);
                    }
                }
            }

            if (currentLine.Length > 0)
            {
                if (font.MeasureText(currentLine) > maxWidth)
                    currentLine = TruncateWithEllipsis(currentLine, font, maxWidth);
                result.Add(currentLine);
            }
        }

        return result;
    }

    /// <summary>
    /// Truncate a string to fit within maxWidth, appending "..." if truncated.
    /// </summary>
    private static string TruncateWithEllipsis(string text, SKFont font, float maxWidth)
    {
        const string ellipsis = "...";
        if (font.MeasureText(text) <= maxWidth)
            return text;

        for (var len = text.Length - 1; len > 0; len--)
        {
            var truncated = text[..len] + ellipsis;
            if (font.MeasureText(truncated) <= maxWidth)
                return truncated;
        }

        return ellipsis;
    }

    /// <summary>
    /// Split text on newlines and truncate each line individually with ellipsis
    /// if it exceeds maxWidth. No word-wrapping — each logical line stays on one line.
    /// </summary>
    private static List<string> TruncateLines(string text, SKFont font, float maxWidth)
    {
        var result = new List<string>();
        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            result.Add(font.MeasureText(trimmed) > maxWidth
                ? TruncateWithEllipsis(trimmed, font, maxWidth)
                : trimmed);
        }
        return result;
    }

    private static void DrawBar(SKCanvas canvas, DisplaySlot slot,
        in GroupDisplayState group, int groupIndex,
        float x, float y, float w, float h)
    {
        var trackColor = BindingResolver.ResolveColor(slot.TrackColor, group, groupIndex, DefaultTrack);
        var fillColor = BindingResolver.ResolveColor(slot.Color ?? slot.Fill, group, groupIndex, SKColors.White);
        fillColor = BindingResolver.WithOpacity(fillColor, slot.Opacity);

        var value = BindingResolver.ResolveFloat(slot.Value, group, groupIndex);
        value = Math.Clamp(value, 0, 100);

        var rect = new SKRect(x, y, x + w, y + h);
        var r = slot.Radius;

        // Track
        using var trackPaint = new SKPaint { Color = trackColor, IsAntialias = true };
        canvas.DrawRoundRect(rect, r, r, trackPaint);

        // Fill
        if (value > 0)
        {
            using var fillPaint = new SKPaint { Color = fillColor, IsAntialias = true };

            if (slot.Direction == BarDirection.Up)
            {
                var fillH = h * value / 100f;
                var fillRect = new SKRect(x, y + h - fillH, x + w, y + h);
                canvas.DrawRoundRect(fillRect, r, r, fillPaint);

                // Square off top corners when fill doesn't reach the top
                if (fillH < h - r)
                {
                    var clipRect = new SKRect(x, y + h - fillH, x + w, y + h - fillH + r);
                    canvas.DrawRect(clipRect, fillPaint);
                }
            }
            else // Right
            {
                var fillW = w * value / 100f;
                var fillRect = new SKRect(x, y, x + fillW, y + h);
                canvas.DrawRoundRect(fillRect, r, r, fillPaint);

                if (fillW < w - r)
                {
                    var clipRect = new SKRect(x + fillW - r, y, x + fillW, y + h);
                    canvas.DrawRect(clipRect, fillPaint);
                }
            }
        }
    }

    private static void DrawCheckbox(SKCanvas canvas, DisplaySlot slot,
        in GroupDisplayState group, int groupIndex,
        float x, float y, string? defaultFontFamily)
    {
        var isChecked = BindingResolver.ResolveBool(slot.Checked, group, groupIndex);
        var label = BindingResolver.ResolveString(slot.Label, group, groupIndex);

        var checkedColor = BindingResolver.ResolveColor(slot.CheckedColor, group, groupIndex, new SKColor(0xE6, 0x39, 0x46));
        var uncheckedColor = BindingResolver.ResolveColor(slot.UncheckedColor, group, groupIndex, new SKColor(0xAA, 0xAA, 0xAA));

        var activeColor = isChecked ? checkedColor : uncheckedColor;

        const int boxSize = 18;
        const int textGap = 6;

        var fontStyle = isChecked ? SKFontStyle.Bold : SKFontStyle.Normal;
        var typeface = ResolveTypeface(slot.FontFamily, defaultFontFamily, fontStyle);
        using var font = new SKFont(typeface, slot.FontSize > 0 ? slot.FontSize : 16);
        using var textPaint = new SKPaint { Color = activeColor, IsAntialias = true };

        // Measure to center
        var textWidth = string.IsNullOrEmpty(label) ? 0 : font.MeasureText(label);
        var totalWidth = boxSize + (string.IsNullOrEmpty(label) ? 0 : textGap + textWidth);
        var startX = x - totalWidth / 2;

        // Box
        var boxRect = new SKRect(startX, y, startX + boxSize, y + boxSize);
        using var boxPaint = new SKPaint
        {
            Color = activeColor,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2
        };
        canvas.DrawRoundRect(boxRect, 3, 3, boxPaint);

        // Checkmark
        if (isChecked)
        {
            using var checkPaint = new SKPaint
            {
                Color = activeColor,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.5f,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round
            };
            using var path = new SKPath();
            path.MoveTo(startX + 4, y + boxSize / 2f);
            path.LineTo(startX + boxSize / 2.5f, y + boxSize - 5);
            path.LineTo(startX + boxSize - 4, y + 5);
            canvas.DrawPath(path, checkPaint);
        }

        // Label
        if (!string.IsNullOrEmpty(label))
        {
            canvas.DrawText(label, startX + boxSize + textGap, y + boxSize - 3,
                SKTextAlign.Left, font, textPaint);
        }
    }

    private static void DrawLine(SKCanvas canvas, DisplaySlot slot,
        in GroupDisplayState group, int groupIndex,
        float x, float y, float w, float h)
    {
        var color = BindingResolver.ResolveColor(slot.Color, group, groupIndex, new SKColor(0x44, 0x44, 0x44));
        using var paint = new SKPaint
        {
            Color = color,
            StrokeWidth = slot.StrokeWidth,
            IsAntialias = true
        };
        canvas.DrawLine(x, y, x + w, y + h, paint);
    }

    private static void DrawArc(SKCanvas canvas, DisplaySlot slot,
        in GroupDisplayState group, int groupIndex,
        float x, float y, float w, float h)
    {
        var trackColor = BindingResolver.ResolveColor(slot.TrackColor, group, groupIndex, new SKColor(0x2A, 0x2A, 0x2A));
        var fillColor = BindingResolver.ResolveColor(slot.Color ?? slot.Fill, group, groupIndex, SKColors.White);
        fillColor = BindingResolver.WithOpacity(fillColor, slot.Opacity);

        var value = BindingResolver.ResolveFloat(slot.Value, group, groupIndex);
        value = Math.Clamp(value, 0, 100);

        var strokeWidth = slot.StrokeWidth > 0 ? slot.StrokeWidth : 10;
        var startAngle = slot.StartAngle;  // e.g. 150
        var sweepAngle = slot.SweepAngle;  // e.g. 240

        // Inset the oval rect by half the stroke width so the stroke fits inside
        var inset = strokeWidth / 2;
        var ovalRect = new SKRect(x + inset, y + inset, x + w - inset, y + h - inset);

        // Draw track arc (the full horseshoe background)
        using var trackPaint = new SKPaint
        {
            Color = trackColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true
        };

        using (var trackPath = new SKPath())
        {
            trackPath.AddArc(ovalRect, startAngle, sweepAngle);
            canvas.DrawPath(trackPath, trackPaint);
        }

        // Draw fill arc (portion based on value)
        if (value > 0)
        {
            var fillSweep = sweepAngle * value / 100f;

            using var fillPaint = new SKPaint
            {
                Color = fillColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth,
                StrokeCap = SKStrokeCap.Round,
                IsAntialias = true
            };

            using var fillPath = new SKPath();
            fillPath.AddArc(ovalRect, startAngle, fillSweep);
            canvas.DrawPath(fillPath, fillPaint);
        }
    }

    private static void DrawImage(SKCanvas canvas, DisplaySlot slot,
        in GroupDisplayState group, int groupIndex,
        float x, float y, float w, float h)
    {
        var src = BindingResolver.ResolveString(slot.Src, group, groupIndex);
        if (string.IsNullOrEmpty(src)) return;

        var img = LoadImage(src);
        if (img is null) return;

        var dest = new SKRect(x, y, x + (w > 0 ? w : img.Width), y + (h > 0 ? h : img.Height));

        using var paint = new SKPaint { IsAntialias = true };

        if (slot.Opacity < 1f)
            paint.Color = new SKColor(255, 255, 255, (byte)(255 * Math.Clamp(slot.Opacity, 0f, 1f)));

        canvas.DrawImage(img, dest, SKSamplingOptions.Default, paint);
    }

    // ── Image loading ───────────────────────────────────────────────

    /// <summary>
    /// Load an image from disk with caching. Resolves relative paths against
    /// the bundled Layouts folder and the config folder. Images outside these
    /// two directories are rejected.
    /// </summary>
    private static SKImage? LoadImage(string path)
    {
        var resolvedPath = ResolveSafeImagePath(path);
        if (resolvedPath is null) return null;

        return ImageCache.GetOrAdd(resolvedPath, static p =>
        {
            try
            {
                if (!File.Exists(p)) return null;
                using var stream = File.OpenRead(p);
                using var data = SKData.Create(stream);
                return SKImage.FromEncodedData(data);
            }
            catch
            {
                return null;
            }
        });
    }

    /// <summary>
    /// Resolve an image path, ensuring it falls within the bundled Layouts folder
    /// or the VolMon config folder. Returns null if the path escapes both.
    /// </summary>
    private static string? ResolveSafeImagePath(string path)
    {
        var layoutsDir = Path.GetFullPath(DisplayLayout.GetBundledLayoutsDir());
        var configDir = Path.GetFullPath(DisplayLayout.GetConfigDir());

        // Try resolving relative to Layouts dir first, then config dir
        string[] candidates = Path.IsPathRooted(path)
            ? [Path.GetFullPath(path)]
            : [
                Path.GetFullPath(Path.Combine(layoutsDir, path)),
                Path.GetFullPath(Path.Combine(configDir, path))
            ];

        foreach (var candidate in candidates)
        {
            if (candidate.StartsWith(layoutsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || candidate.StartsWith(configDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    // ── Font loading ──────────────────────────────────────────────────

    /// <summary>
    /// Resolve a typeface from a slot-level font family, a layout-level default, or
    /// the built-in "sans-serif" fallback. Supports:
    /// <list type="bullet">
    ///   <item>System font family names (e.g. "monospace", "serif", "Noto Sans")</item>
    ///   <item>Paths to .ttf/.otf files relative to the Layouts/ or config folder</item>
    /// </list>
    /// For font file paths, bold weight is resolved automatically by convention:
    /// if the path contains "-Regular", it is replaced with "-Bold"; otherwise
    /// "-Bold" is inserted before the extension (e.g. "Foo.ttf" → "Foo-Bold.ttf").
    /// If the bold variant file doesn't exist, the regular file is used and
    /// SkiaSharp will apply synthetic bolding.
    ///
    /// Results are cached so each unique font is loaded from disk at most once.
    /// </summary>
    private static SKTypeface ResolveTypeface(string? slotFont, string? layoutFont, SKFontStyle style)
    {
        var fontName = slotFont ?? layoutFont;

        if (string.IsNullOrEmpty(fontName))
            return SKTypeface.FromFamilyName("sans-serif", style);

        // Check if this looks like a file path (has a font file extension)
        if (IsFontFilePath(fontName))
        {
            // For bold weight, try to find a bold variant file first
            if (style.Weight >= (int)SKFontStyleWeight.SemiBold)
            {
                var boldPath = DeriveBoldPath(fontName);
                if (boldPath is not null)
                {
                    var boldTypeface = LoadTypefaceFromFile(boldPath);
                    if (boldTypeface is not null)
                        return boldTypeface;
                }
            }

            var typeface = LoadTypefaceFromFile(fontName);
            if (typeface is not null)
                return typeface;

            // File not found or failed to load — fall back to system lookup
        }

        // System font family lookup — cache by "family|weight|width|slant"
        var cacheKey = $"family:{fontName}|{style.Weight}|{style.Width}|{style.Slant}";
        return TypefaceCache.GetOrAdd(cacheKey, _ =>
            SKTypeface.FromFamilyName(fontName, style)) ?? SKTypeface.FromFamilyName("sans-serif", style);
    }

    /// <summary>
    /// Derive the bold variant file path from a regular font file path.
    /// "Fonts/Montserrat-Regular.ttf" → "Fonts/Montserrat-Bold.ttf"
    /// "Fonts/MyFont.ttf" → "Fonts/MyFont-Bold.ttf"
    /// </summary>
    private static string? DeriveBoldPath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        // Replace -Regular/-regular with -Bold
        string boldName;
        if (name.EndsWith("-Regular", StringComparison.OrdinalIgnoreCase))
            boldName = name[..^"-Regular".Length] + "-Bold";
        else
            boldName = name + "-Bold";

        return Path.Combine(dir, boldName + ext);
    }

    /// <summary>
    /// Check if a font name looks like a file path (has .ttf, .otf, or .woff2 extension).
    /// </summary>
    private static bool IsFontFilePath(string name) =>
        name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Load a typeface from a font file on disk with caching. Uses the same
    /// safe-path resolution as image loading (Layouts/ folder and config folder).
    /// </summary>
    private static SKTypeface? LoadTypefaceFromFile(string path)
    {
        var resolvedPath = ResolveSafeImagePath(path); // Same path rules as images
        if (resolvedPath is null) return null;

        return TypefaceCache.GetOrAdd(resolvedPath, static p =>
        {
            try
            {
                if (!File.Exists(p)) return null;
                return SKTypeface.FromFile(p);
            }
            catch
            {
                return null;
            }
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static (int start, int end) ParseRange(string range)
    {
        var parts = range.Split('-');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var s) &&
            int.TryParse(parts[1], out var e))
            return (s, e);

        return (0, 3);
    }
}
