using System.Text.RegularExpressions;
using SkiaSharp;

namespace VolMon.Hardware.Beacn.Mix.Display;

/// <summary>
/// Resolves data bindings in layout slot properties.
///
/// Binding syntax:
///   {group.name}              -> group name string
///   {group.volume}            -> volume integer (0-100)
///   {group.volume}%           -> "75%"
///   {group.muted}             -> "True" / "False"
///   {group.color}             -> hex color string
///   {group.index}             -> 0-based group index
///   {group.dial}              -> 1-based dial number
///   {group.activeMembers}     -> newline-separated active member names
///   {group.inactiveMembers}   -> newline-separated inactive member names
///   {group.hasActiveMembers}  -> "True" if any members are active
///   {group.hasInactiveMembers} -> "True" if any members are inactive
///
/// Plain strings without {} are returned as-is.
/// </summary>
internal static partial class BindingResolver
{
    [GeneratedRegex(@"\{group\.(\w+)\}")]
    private static partial Regex BindingPattern();

    /// <summary>
    /// Resolve a string that may contain {group.*} bindings.
    /// </summary>
    public static string ResolveString(string? template, in GroupDisplayState group, int groupIndex)
    {
        if (string.IsNullOrEmpty(template))
            return string.Empty;

        // Fast path: no binding
        if (!template.Contains('{'))
            return template;

        // Copy to local so the lambda can capture it (can't capture 'in' params)
        var g = group;
        var idx = groupIndex;

        return BindingPattern().Replace(template, match =>
        {
            var prop = match.Groups[1].Value;
            return prop switch
            {
                "name" => g.Name,
                "volume" => g.Volume.ToString(),
                "muted" => g.Muted.ToString(),
                "color" => g.Color ?? "#FFFFFF",
                "index" => idx.ToString(),
                "dial" => (idx + 1).ToString(),
                "activeMembers" => FormatMembers(g.ActiveMembers),
                "inactiveMembers" => FormatMembers(g.InactiveMembers),
                "hasActiveMembers" => (g.ActiveMembers.Length > 0).ToString(),
                "hasInactiveMembers" => (g.InactiveMembers.Length > 0).ToString(),
                _ => match.Value
            };
        });
    }

    /// <summary>
    /// Resolve a string to a float value. Supports plain numbers and bindings.
    /// </summary>
    public static float ResolveFloat(string? template, in GroupDisplayState group, int groupIndex, float fallback = 0)
    {
        if (string.IsNullOrEmpty(template))
            return fallback;

        var resolved = ResolveString(template, group, groupIndex);
        return float.TryParse(resolved, out var val) ? val : fallback;
    }

    /// <summary>
    /// Resolve a string to an integer.
    /// </summary>
    public static int ResolveInt(string? template, in GroupDisplayState group, int groupIndex, int fallback = 0)
    {
        if (string.IsNullOrEmpty(template))
            return fallback;

        var resolved = ResolveString(template, group, groupIndex);
        return int.TryParse(resolved, out var val) ? val : fallback;
    }

    /// <summary>
    /// Resolve a string to a boolean.
    /// </summary>
    public static bool ResolveBool(string? template, in GroupDisplayState group, int groupIndex, bool fallback = false)
    {
        if (string.IsNullOrEmpty(template))
            return fallback;

        var resolved = ResolveString(template, group, groupIndex);
        return bool.TryParse(resolved, out var val) ? val : fallback;
    }

    /// <summary>
    /// Resolve a color string. Supports hex colors and {group.color} binding.
    /// </summary>
    public static SKColor ResolveColor(string? template, in GroupDisplayState group, int groupIndex, SKColor fallback)
    {
        if (string.IsNullOrEmpty(template))
            return fallback;

        var resolved = ResolveString(template, group, groupIndex);
        return SKColor.TryParse(resolved, out var color) ? color : fallback;
    }

    /// <summary>
    /// Apply opacity to a color.
    /// </summary>
    public static SKColor WithOpacity(SKColor color, float opacity)
    {
        if (opacity >= 1f) return color;
        var alpha = (byte)(color.Alpha * Math.Clamp(opacity, 0f, 1f));
        return new SKColor(color.Red, color.Green, color.Blue, alpha);
    }

    /// <summary>
    /// Format a list of member names with one entry per line for display.
    /// </summary>
    private static string FormatMembers(string[]? members) =>
        members is null || members.Length == 0 ? "" : string.Join("\n", members);

}
