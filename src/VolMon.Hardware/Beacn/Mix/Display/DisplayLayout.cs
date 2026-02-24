using System.Text.Json;
using System.Text.Json.Serialization;

namespace VolMon.Hardware.Beacn.Mix.Display;

/// <summary>
/// Root model for a display layout template.
/// A layout contains global settings and a list of slots to render.
/// </summary>
public sealed class DisplayLayout
{
    /// <summary>Display width in pixels.</summary>
    public int Width { get; set; } = 800;

    /// <summary>Display height in pixels.</summary>
    public int Height { get; set; } = 480;

    /// <summary>Background color as hex string (e.g. "#2B2B2B").</summary>
    public string Background { get; set; } = "#2B2B2B";

    /// <summary>JPEG encode quality (1-100). Lower = smaller/faster transfer.</summary>
    public int JpegQuality { get; set; } = 50;

    /// <summary>
    /// Optional background image path (PNG or JPEG). Drawn after the background color
    /// and before all slots, scaled to fill the display. Path is relative to the layout
    /// file's directory, the Layouts/ folder, or absolute.
    /// </summary>
    public string? BackgroundImage { get; set; }

    /// <summary>The list of visual slots to render, drawn in order (back to front).</summary>
    public List<DisplaySlot> Slots { get; set; } = [];

    // ── Serialization ───────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Get the VolMon config directory (~/.config/volmon/ on Linux).
    /// </summary>
    public static string GetConfigDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
            appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return Path.Combine(appData, "volmon");
    }

    /// <summary>
    /// Get the bundled Layouts directory next to the running executable.
    /// </summary>
    public static string GetBundledLayoutsDir()
    {
        var exeDir = AppContext.BaseDirectory;
        return Path.Combine(exeDir, "Layouts");
    }

    /// <summary>
    /// Load a layout by name.
    /// Resolution order:
    ///   1. Bundled preset — checks for "{layoutName}.json" in the Layouts/ folder
    ///   2. Config folder — checks for "{layoutName}.json" in ~/.config/volmon/
    ///   3. Hardcoded default
    ///
    /// Files outside the bundled Layouts/ folder and the config folder are never read.
    /// </summary>
    public static async Task<DisplayLayout> LoadAsync(string layoutName = "VolMon_Layout_BeacnMix_default-vertical")
    {
        // Sanitize: strip path separators to prevent directory traversal
        var safeName = Path.GetFileNameWithoutExtension(layoutName);
        if (string.IsNullOrWhiteSpace(safeName))
            return DefaultLayout.Create();

        // 1. Bundled preset
        var bundledPath = Path.Combine(GetBundledLayoutsDir(), $"{safeName}.json");
        if (File.Exists(bundledPath))
            return await LoadFromFileAsync(bundledPath);

        // 2. Custom layout in config folder
        var configPath = Path.Combine(GetConfigDir(), $"{safeName}.json");
        var fullConfigPath = Path.GetFullPath(configPath);
        var fullConfigDir = Path.GetFullPath(GetConfigDir());

        // Ensure the resolved path is still inside the config directory
        if (fullConfigPath.StartsWith(fullConfigDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && File.Exists(fullConfigPath))
        {
            return await LoadFromFileAsync(fullConfigPath);
        }

        // 3. Hardcoded fallback
        return DefaultLayout.Create();
    }

    /// <summary>
    /// List available bundled layout preset names (full filenames without extension).
    /// </summary>
    public static string[] ListBundledLayouts()
    {
        var dir = GetBundledLayoutsDir();
        if (!Directory.Exists(dir))
            return [];

        return Directory.GetFiles(dir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n)
            .ToArray();
    }

    private static async Task<DisplayLayout> LoadFromFileAsync(string path)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<DisplayLayout>(json, JsonOptions)
                ?? DefaultLayout.Create();
        }
        catch
        {
            return DefaultLayout.Create();
        }
    }


}

/// <summary>
/// A single visual element in the display layout.
/// </summary>
public sealed class DisplaySlot
{
    /// <summary>What kind of visual element this slot is.</summary>
    public SlotType Type { get; set; }

    /// <summary>X position (pixels from left). Supports bindings.</summary>
    public string X { get; set; } = "0";

    /// <summary>Y position (pixels from top). Supports bindings.</summary>
    public string Y { get; set; } = "0";

    /// <summary>Width in pixels. Supports bindings.</summary>
    public string? W { get; set; }

    /// <summary>Height in pixels. Supports bindings.</summary>
    public string? H { get; set; }

    // ── Image properties ────────────────────────────────────────────

    /// <summary>
    /// Image file path for Image slots. Relative to the Layouts/ folder, or absolute.
    /// Supports PNG and JPEG.
    /// </summary>
    public string? Src { get; set; }

    // ── Common properties ───────────────────────────────────────────

    /// <summary>Color as hex string or binding (e.g. "#FFFFFF" or "{group.color}").</summary>
    public string? Color { get; set; }

    /// <summary>Background/fill color as hex string or binding.</summary>
    public string? Fill { get; set; }

    /// <summary>Opacity (0.0-1.0). Supports bindings like "{group.muted ? 0.4 : 1.0}".</summary>
    public float Opacity { get; set; } = 1.0f;

    /// <summary>Corner radius for rounded rectangles/bars.</summary>
    public float Radius { get; set; }

    // ── Text properties ─────────────────────────────────────────────

    /// <summary>Text content or binding (e.g. "{group.name}" or "{group.volume}%").</summary>
    public string? Text { get; set; }

    /// <summary>Font size in pixels.</summary>
    public float FontSize { get; set; } = 16;

    /// <summary>Font weight: normal or bold.</summary>
    public FontWeight FontWeight { get; set; } = FontWeight.Normal;

    /// <summary>Text alignment within the slot: left, center, right.</summary>
    public HAlign Align { get; set; } = HAlign.Center;

    // ── Bar properties ──────────────────────────────────────────────

    /// <summary>Value for bars (0-100 or binding like "{group.volume}").</summary>
    public string? Value { get; set; }

    /// <summary>Bar track/background color.</summary>
    public string? TrackColor { get; set; }

    /// <summary>Bar direction: up (bottom-to-top) or right (left-to-right).</summary>
    public BarDirection Direction { get; set; } = BarDirection.Up;

    // ── Checkbox properties ─────────────────────────────────────────

    /// <summary>Boolean binding for checked state (e.g. "{group.muted}").</summary>
    public string? Checked { get; set; }

    /// <summary>Label text for checkbox.</summary>
    public string? Label { get; set; }

    /// <summary>Color when checked.</summary>
    public string? CheckedColor { get; set; }

    /// <summary>Color when unchecked.</summary>
    public string? UncheckedColor { get; set; }

    // ── Arc properties ──────────────────────────────────────────────

    /// <summary>
    /// Start angle in degrees for arc rendering. 0 = right (3 o'clock), 90 = bottom.
    /// For a horseshoe opening at the bottom with 1/6 gap: startAngle = 150.
    /// </summary>
    public float StartAngle { get; set; } = 150;

    /// <summary>
    /// Total sweep angle in degrees for the arc track (the full arc extent).
    /// For a horseshoe with 1/6 gap (300 degrees of arc): sweepAngle = 240.
    /// </summary>
    public float SweepAngle { get; set; } = 240;

    // ── Line properties ─────────────────────────────────────────────

    /// <summary>Stroke width for lines and outlines.</summary>
    public float StrokeWidth { get; set; } = 1;

    // ── Repeat / group binding ──────────────────────────────────────

    /// <summary>
    /// If set, this slot is repeated for each group index in this range.
    /// Format: "0-3" means repeat for groups 0, 1, 2, 3.
    /// Inside the slot, use {group.*} bindings which resolve to the current group.
    /// </summary>
    public string? Repeat { get; set; }

    /// <summary>
    /// X offset added per repeat iteration. E.g. "200" shifts each group column 200px right.
    /// </summary>
    public string? RepeatOffsetX { get; set; }

    /// <summary>
    /// Y offset added per repeat iteration.
    /// </summary>
    public string? RepeatOffsetY { get; set; }

    /// <summary>
    /// Child slots (only used with repeat). These inherit the repeat context.
    /// </summary>
    public List<DisplaySlot>? Children { get; set; }
}

public enum SlotType
{
    Rect,
    Text,
    Bar,
    Checkbox,
    Line,
    Arc,
    Image
}

public enum FontWeight
{
    Normal,
    Bold
}

public enum HAlign
{
    Left,
    Center,
    Right
}

public enum BarDirection
{
    Up,
    Right
}
