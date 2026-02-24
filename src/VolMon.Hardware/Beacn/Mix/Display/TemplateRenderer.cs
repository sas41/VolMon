using System.Collections.Concurrent;
using SkiaSharp;

namespace VolMon.Hardware.Beacn.Mix.Display;

/// <summary>
/// Renders a DisplayLayout template to a JPEG byte array by walking the slot tree,
/// resolving data bindings, and drawing each element with SkiaSharp.
/// </summary>
internal static class TemplateRenderer
{
    private static readonly SKColor DefaultText = new(0xFF, 0xFF, 0xFF);
    private static readonly SKColor DefaultTrack = new(0x3A, 0x3A, 0x3A);

    /// <summary>
    /// Cache loaded images by absolute path so we don't hit disk every frame.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SKImage?> ImageCache = new();

    public static byte[] Render(DisplayLayout layout, ReadOnlySpan<GroupDisplayState> groups)
    {
        using var surface = SKSurface.Create(new SKImageInfo(layout.Width, layout.Height));
        var canvas = surface.Canvas;

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
        foreach (var slot in layout.Slots)
        {
            DrawSlot(canvas, slot, groups);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(layout.JpegQuality, 1, 100));
        return data.ToArray();
    }

    private static void DrawSlot(SKCanvas canvas, DisplaySlot slot, ReadOnlySpan<GroupDisplayState> groups)
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
                    DrawSingleSlot(canvas, slot, groups[i], i);

                // Draw children
                if (slot.Children is not null)
                {
                    foreach (var child in slot.Children)
                        DrawSingleSlot(canvas, child, groups[i], i);
                }

                canvas.Restore();
            }
            return;
        }

        // Non-repeating: draw with group 0 context (or empty)
        var g = groups.Length > 0 ? groups[0] : default;
        DrawSingleSlot(canvas, slot, g, 0);
    }

    private static void DrawSingleSlot(SKCanvas canvas, DisplaySlot slot, in GroupDisplayState group, int groupIndex)
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
                DrawText(canvas, slot, group, groupIndex, x, y, w);
                break;
            case SlotType.Bar:
                DrawBar(canvas, slot, group, groupIndex, x, y, w, h);
                break;
            case SlotType.Checkbox:
                DrawCheckbox(canvas, slot, group, groupIndex, x, y);
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
        float x, float y, float w)
    {
        var text = BindingResolver.ResolveString(slot.Text, group, groupIndex);
        if (string.IsNullOrEmpty(text)) return;

        var color = BindingResolver.ResolveColor(slot.Color, group, groupIndex, DefaultText);
        color = BindingResolver.WithOpacity(color, slot.Opacity);

        var fontStyle = slot.FontWeight == FontWeight.Bold ? SKFontStyle.Bold : SKFontStyle.Normal;
        using var font = new SKFont(SKTypeface.FromFamilyName("sans-serif", fontStyle), slot.FontSize);
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

        // y is the top of the text area; SkiaSharp draws from baseline
        var drawY = y + font.Size;

        canvas.DrawText(text, drawX, drawY, textAlign, font, paint);
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
        float x, float y)
    {
        var isChecked = BindingResolver.ResolveBool(slot.Checked, group, groupIndex);
        var label = BindingResolver.ResolveString(slot.Label, group, groupIndex);

        var checkedColor = BindingResolver.ResolveColor(slot.CheckedColor, group, groupIndex, new SKColor(0xE6, 0x39, 0x46));
        var uncheckedColor = BindingResolver.ResolveColor(slot.UncheckedColor, group, groupIndex, new SKColor(0xAA, 0xAA, 0xAA));

        var activeColor = isChecked ? checkedColor : uncheckedColor;

        const int boxSize = 18;
        const int textGap = 6;

        var fontStyle = isChecked ? SKFontStyle.Bold : SKFontStyle.Normal;
        using var font = new SKFont(SKTypeface.FromFamilyName("sans-serif", fontStyle), slot.FontSize > 0 ? slot.FontSize : 16);
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
