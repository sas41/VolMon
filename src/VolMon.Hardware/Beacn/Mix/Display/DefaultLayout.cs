namespace VolMon.Hardware.Beacn.Mix.Display;

/// <summary>
/// Creates the default display layout that replicates the original hardcoded renderer:
/// 4 group columns, each with name, color bar, vertical volume bar, volume %, mute checkbox.
/// </summary>
internal static class DefaultLayout
{
    public static DisplayLayout Create() => new()
    {
        Width = 800,
        Height = 480,
        Background = "#2B2B2B",
        JpegQuality = 50,
        Slots =
        [
            // Column separators (vertical lines between groups)
            new DisplaySlot
            {
                Type = SlotType.Line,
                X = "200", Y = "0", W = "0", H = "480",
                Color = "#444444", StrokeWidth = 1
            },
            new DisplaySlot
            {
                Type = SlotType.Line,
                X = "400", Y = "0", W = "0", H = "480",
                Color = "#444444", StrokeWidth = 1
            },
            new DisplaySlot
            {
                Type = SlotType.Line,
                X = "600", Y = "0", W = "0", H = "480",
                Color = "#444444", StrokeWidth = 1
            },

            // Repeated group column (repeats for groups 0-3, offset 200px each)
            new DisplaySlot
            {
                Repeat = "0-3",
                RepeatOffsetX = "200",
                Children =
                [
                    // Group name
                    new DisplaySlot
                    {
                        Type = SlotType.Text,
                        X = "0", Y = "16", W = "200",
                        Text = "{group.name}",
                        Color = "#FFFFFF",
                        FontSize = 22,
                        FontWeight = FontWeight.Bold,
                        Align = HAlign.Center
                    },

                    // Color accent bar
                    new DisplaySlot
                    {
                        Type = SlotType.Rect,
                        X = "12", Y = "48", W = "176", H = "4",
                        Fill = "{group.color}",
                        Radius = 2
                    },

                    // Volume bar (track + fill)
                    new DisplaySlot
                    {
                        Type = SlotType.Bar,
                        X = "70", Y = "68", W = "60", H = "332",
                        Value = "{group.volume}",
                        Color = "{group.color}",
                        TrackColor = "#3A3A3A",
                        Direction = BarDirection.Up,
                        Radius = 6
                    },

                    // Volume percentage text
                    new DisplaySlot
                    {
                        Type = SlotType.Text,
                        X = "0", Y = "416", W = "200",
                        Text = "{group.volume}%",
                        Color = "#AAAAAA",
                        FontSize = 18,
                        Align = HAlign.Center
                    },

                    // Mute checkbox
                    new DisplaySlot
                    {
                        Type = SlotType.Checkbox,
                        X = "100", Y = "448",
                        Checked = "{group.muted}",
                        Label = "Muted",
                        CheckedColor = "#E63946",
                        UncheckedColor = "#AAAAAA",
                        FontSize = 16
                    }
                ]
            }
        ]
    };
}
