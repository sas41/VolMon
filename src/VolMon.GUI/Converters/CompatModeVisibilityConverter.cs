using System.Globalization;
using Avalonia.Data.Converters;
using VolMon.Core.Audio;

namespace VolMon.GUI.Converters;

/// <summary>
/// Returns <c>true</c> (visible) only when the bound value is
/// <see cref="GroupMode.Compatibility"/> AND the current platform supports
/// virtual null-sinks (Linux/PulseAudio/PipeWire).
/// On Windows and macOS this always returns <c>false</c> so any icon or
/// indicator tied to Compatibility Mode is hidden unconditionally.
/// </summary>
public sealed class CompatModeVisibilityConverter : IValueConverter
{
    public static readonly CompatModeVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        OperatingSystem.IsLinux() && value is GroupMode.Compatibility;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
