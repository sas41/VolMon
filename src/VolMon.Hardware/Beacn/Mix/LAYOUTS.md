# Beacn Mix Display Layouts

A layout is a JSON file that defines everything shown on the Beacn Mix 800x480
LCD. Layouts are fully declarative: you define a list of visual elements
("slots") with positions, sizes, colors, and data bindings. The renderer draws
them in order (back to front) and sends the result as JPEG to the device.

## Quick Start

1. Copy an existing layout from `Layouts/` to your config directory (see paths below).
2. Rename it (e.g. `my-layout.json`).
3. Edit your device config (`beacn-mix-{serial}.json` in the config directory) and set:
   ```json
   { "layout": "my-layout" }
   ```
4. Save -- the display updates live, no restart needed.

## File Locations

### Config Directory

The config directory is determined by your operating system:

| OS | Config directory |
|---|---|
| Linux | `~/.config/volmon/` |
| macOS | `~/Library/Application Support/volmon/` |
| Windows | `%APPDATA%\volmon\` (typically `C:\Users\{user}\AppData\Roaming\volmon\`) |

### All Paths

| Purpose | Path |
|---|---|
| Bundled layouts | `{exe}/Layouts/*.json` |
| Bundled fonts | `{exe}/Layouts/Fonts/` |
| Custom layouts | `{config}/my-layout.json` |
| Custom fonts/images | `{config}/` |
| Per-device config | `{config}/beacn-mix-{serial}.json` |

Where `{config}` is the config directory for your OS (see above) and `{exe}` is
the directory containing the VolMon executable.

Layout names are filenames without the `.json` extension. The bundled layouts
directory is checked first, then the config directory.

## Bundled Layouts

| Name | Description |
|---|---|
| `VolMon_Layout_BeacnMix_horseshoe-gauges` | **Default.** Horseshoe arc gauges with volume numbers, member lists, and mute checkboxes. |
| `VolMon_Layout_BeacnMix_horizontal-bars` | Horizontal progress bars with group names and members. |
| `VolMon_Layout_BeacnMix_compact-grid` | 2x2 grid, 4 groups in quadrants with bars and member lists. |
| `VolMon_Layout_BeacnMix_default-vertical` | Vertical bars, minimal -- no member lists. |

## Layout Structure

```json
{
  "width": 800,
  "height": 480,
  "background": "#111111",
  "jpegQuality": 50,
  "fontFamily": "Fonts/Montserrat-Regular.ttf",
  "backgroundImage": null,
  "slots": [ ... ]
}
```

### Root Properties

| Property | Type | Default | Description |
|---|---|---|---|
| `width` | int | `800` | Display width in pixels. |
| `height` | int | `480` | Display height in pixels. |
| `background` | string | `"#2B2B2B"` | Background color (hex). |
| `backgroundImage` | string | `null` | Optional background image (PNG/JPEG). Relative to `Layouts/` or config dir. Scaled to fill the display. |
| `jpegQuality` | int | `50` | JPEG encode quality (1-100). Lower = faster USB transfer. |
| `fontFamily` | string | `"Fonts/Montserrat-Regular.ttf"` | Default font for all text. Can be a system font name or path to a `.ttf`/`.otf` file. Set to `null` to use the system sans-serif. |
| `slots` | array | `[]` | Visual elements to render, drawn in order (back to front). |

## Slot Types

Every slot has a `type` field that determines what it draws. The 7 slot types
are:

| Type | Description |
|---|---|
| `rect` | Filled rectangle (or rounded rectangle). |
| `text` | Text label with optional wrapping, truncation, and secondary text. |
| `bar` | Progress/volume bar (horizontal or vertical). |
| `arc` | Circular/horseshoe gauge. |
| `checkbox` | Checkbox with label (e.g. mute indicator). |
| `line` | Straight line (horizontal, vertical, or diagonal). |
| `image` | PNG or JPEG image from disk. |

## Common Slot Properties

These properties are available on all slot types:

| Property | Type | Default | Description |
|---|---|---|---|
| `type` | string | *required* | Slot type (see above). |
| `x` | string | `"0"` | X position in pixels. Supports bindings. |
| `y` | string | `"0"` | Y position in pixels. Supports bindings. |
| `w` | string | `null` | Width in pixels. Supports bindings. |
| `h` | string | `null` | Height in pixels. Supports bindings. |
| `color` | string | `null` | Primary color (hex or binding, e.g. `"#FFFFFF"` or `"{group.color}"`). |
| `fill` | string | `null` | Fill/background color (hex or binding). |
| `opacity` | float | `1.0` | Opacity (0.0 to 1.0). |
| `radius` | float | `0` | Corner radius for rounded shapes. |

## Slot Type Reference

### Rect

A filled rectangle. Uses `fill` (falls back to `color`).

```json
{
  "type": "rect",
  "x": "60", "y": "46", "w": "80", "h": "3",
  "fill": "{group.color}",
  "radius": 2
}
```

### Text

Text label with full rendering control.

```json
{
  "type": "text",
  "x": "0", "y": "14", "w": "200",
  "text": "{group.name}",
  "color": "#DDDDDD",
  "fontSize": 28,
  "fontWeight": "bold",
  "align": "center"
}
```

#### Text Properties

| Property | Type | Default | Description |
|---|---|---|---|
| `text` | string | `null` | Text content or binding. |
| `secondaryText` | string | `null` | Secondary text, rendered immediately after primary text in `secondaryColor`. No gap between them. |
| `color` | string | `"#FFFFFF"` | Primary text color. |
| `secondaryColor` | string | *(primary)* | Color for secondary text. Falls back to `color` if not set. |
| `fontFamily` | string | *(layout)* | Font override for this slot. Path to `.ttf`/`.otf` or system font name. |
| `fontSize` | float | `16` | Font size in pixels. |
| `fontWeight` | string | `"normal"` | `"normal"` or `"bold"`. For file-based fonts, the bold variant is auto-resolved (e.g. `Montserrat-Regular.ttf` -> `Montserrat-Bold.ttf`). |
| `align` | string | `"center"` | `"left"`, `"center"`, or `"right"`. |
| `overflow` | string | `"wrap"` | `"wrap"` (word-wrap onto multiple lines) or `"truncate"` (clip each line with "..."). |
| `separatorColor` | string | `null` | If set, draws a thin horizontal line in this color between each text line. |

#### Text Overflow Modes

**`wrap`** (default): Text is word-wrapped to fit within the slot width.
Explicit `\n` in the resolved text forces line breaks. Individual words that
still exceed the width are truncated with "...".

**`truncate`**: Each line (split on `\n`) stays on one line. If it exceeds the
slot width, it is clipped with "...". No word-wrapping.

#### Primary + Secondary Text

Use `text` and `secondaryText` to render two groups of data as one continuous
block with different colors. This is how active and inactive group members are
displayed:

```json
{
  "type": "text",
  "x": "6", "y": "290", "w": "188",
  "text": "{group.activeMembers}",
  "secondaryText": "{group.inactiveMembers}",
  "color": "#888888",
  "secondaryColor": "#555555",
  "fontSize": 13,
  "align": "center",
  "overflow": "truncate",
  "separatorColor": "#333333"
}
```

The secondary text is rendered immediately after the last line of the primary
text, continuing the same vertical stack. If primary text is empty (e.g. no
active members), only the secondary text is drawn.

### Bar

A progress/volume bar with a track background and filled portion.

```json
{
  "type": "bar",
  "x": "70", "y": "30", "w": "60", "h": "370",
  "value": "{group.volume}",
  "color": "{group.color}",
  "trackColor": "#3A3A3A",
  "direction": "up",
  "radius": 6
}
```

| Property | Type | Default | Description |
|---|---|---|---|
| `value` | string | `null` | Value 0-100 (clamped). Supports bindings. |
| `trackColor` | string | `"#3A3A3A"` | Background track color. |
| `color`/`fill` | string | `null` | Fill color for the bar. |
| `direction` | string | `"up"` | `"up"` (bottom-to-top) or `"right"` (left-to-right). |
| `radius` | float | `0` | Corner radius for track and fill. |

### Arc

A circular or horseshoe gauge.

```json
{
  "type": "arc",
  "x": "20", "y": "70", "w": "160", "h": "160",
  "value": "{group.volume}",
  "color": "{group.color}",
  "trackColor": "#222222",
  "strokeWidth": 12,
  "startAngle": 120,
  "sweepAngle": 300
}
```

| Property | Type | Default | Description |
|---|---|---|---|
| `value` | string | `null` | Value 0-100 (clamped). Determines fill sweep. |
| `trackColor` | string | `"#2A2A2A"` | Full arc background color. |
| `color`/`fill` | string | `null` | Filled portion color. |
| `strokeWidth` | float | `10` | Arc stroke thickness. |
| `startAngle` | float | `150` | Start angle in degrees. 0 = 3 o'clock, 90 = 6 o'clock. |
| `sweepAngle` | float | `240` | Total arc extent in degrees. |

**Horseshoe example**: `startAngle: 120, sweepAngle: 300` creates a horseshoe
that opens at the bottom with a 60-degree gap.

### Checkbox

A checkbox with an optional label. Useful for mute indicators.

```json
{
  "type": "checkbox",
  "x": "100", "y": "252",
  "checked": "{group.muted}",
  "label": "Muted",
  "checkedColor": "#E63946",
  "uncheckedColor": "#444444",
  "fontSize": 16
}
```

| Property | Type | Default | Description |
|---|---|---|---|
| `checked` | string | `null` | Boolean binding (e.g. `"{group.muted}"`). |
| `label` | string | `null` | Label text next to the checkbox. |
| `checkedColor` | string | `"#E63946"` | Color when checked. |
| `uncheckedColor` | string | `"#AAAAAA"` | Color when unchecked. |
| `fontSize` | float | `16` | Label font size. |

The checkbox is 18px square with rounded corners. When checked, a checkmark is
drawn and the label renders in bold. The checkbox + label are horizontally
centered at the given X position.

### Line

A straight line.

```json
{
  "type": "line",
  "x": "200", "y": "20", "w": "0", "h": "440",
  "color": "#1A1A1A",
  "strokeWidth": 1
}
```

| Property | Type | Default | Description |
|---|---|---|---|
| `color` | string | `"#444444"` | Line color. |
| `strokeWidth` | float | `1` | Line thickness in pixels. |

The line is drawn from `(x, y)` to `(x + w, y + h)`:
- **Vertical line**: `w: "0"`, `h: "440"`.
- **Horizontal line**: `w: "760"`, `h: "0"`.
- **Diagonal**: set both `w` and `h` to non-zero.

### Image

Renders a PNG or JPEG image from disk.

```json
{
  "type": "image",
  "x": "10", "y": "10", "w": "64", "h": "64",
  "src": "icons/logo.png",
  "opacity": 0.8
}
```

| Property | Type | Default | Description |
|---|---|---|---|
| `src` | string | `null` | Image file path. Relative to `Layouts/` or config dir. |
| `w` | string | `null` | Width (0 or omitted = native width). |
| `h` | string | `null` | Height (0 or omitted = native height). |
| `opacity` | float | `1.0` | Image opacity. |

Images are cached in memory after first load.

## Repeat Blocks (Group Iteration)

To render the same elements for each audio group, use a repeat block. This is
the primary mechanism for creating multi-group layouts.

```json
{
  "repeat": "0-3",
  "repeatOffsetX": "200",
  "children": [
    { "type": "text", "x": "0", "y": "14", "w": "200", "text": "{group.name}" },
    { "type": "bar", "x": "70", "y": "50", "w": "60", "h": "300", "value": "{group.volume}" }
  ]
}
```

| Property | Type | Description |
|---|---|---|
| `repeat` | string | Group index range, e.g. `"0-3"` for groups 0, 1, 2, 3. |
| `repeatOffsetX` | string | X offset added per iteration (e.g. `"200"` = each column 200px apart). |
| `repeatOffsetY` | string | Y offset added per iteration (e.g. `"120"` = each row 120px apart). |
| `children` | array | Child slots rendered for each group. All `{group.*}` bindings inside resolve to the current iteration's group. |

The repeat block itself is not drawn -- only its children. Children inherit the
repeat context, so `{group.name}` in the first iteration resolves to group 0's
name, in the second to group 1's name, etc.

You can have multiple repeat blocks with different ranges (e.g. `"0-1"` and
`"2-3"`) for asymmetric layouts like the compact-grid.

## Data Bindings

Any string property can contain `{group.*}` bindings that are resolved at
render time against the current group's data. Multiple bindings can appear in a
single string (e.g. `"{group.volume}%"` becomes `"75%"`).

### Available Variables

| Binding | Type | Description | Example |
|---|---|---|---|
| `{group.name}` | string | Group name | `"Music"` |
| `{group.volume}` | int | Volume level (0-100) | `"75"` |
| `{group.muted}` | bool | Mute state | `"True"` / `"False"` |
| `{group.color}` | string | Group color (hex) | `"#FF6B6B"` |
| `{group.index}` | int | 0-based group index | `"0"` |
| `{group.dial}` | int | 1-based dial number | `"1"` |
| `{group.activeMembers}` | string | Active member names, newline-separated | `"Spotify\nFirefox"` |
| `{group.inactiveMembers}` | string | Inactive member names, newline-separated | `"Discord\nSlack"` |
| `{group.hasActiveMembers}` | bool | True if any members are active | `"True"` / `"False"` |
| `{group.hasInactiveMembers}` | bool | True if any members are inactive | `"True"` / `"False"` |

Bindings work in:
- Text content (`text`, `secondaryText`, `label`)
- Colors (`color`, `fill`, `secondaryColor`, `separatorColor`, etc.)
- Numeric values (`value`, `x`, `y`, `w`, `h`)
- Boolean values (`checked`)

### Active vs Inactive Members

A **program** is "active" if it is currently running -- even if it is not
producing audio. Only programs that are not running at all appear as inactive.

A **device** is "active" if it is present in the system and assigned to the
group. Devices are displayed by their friendly description (e.g. "Rode-NT
Input") rather than their raw system identifier.

## Fonts

### Bundled Font

Montserrat (Regular + Bold) is bundled in `Layouts/Fonts/` and used as the
default for all layouts. Licensed under the SIL Open Font License.

### Custom Fonts

Place `.ttf` or `.otf` files in your config directory (or `Layouts/Fonts/`) and
reference them by relative path:

```json
{
  "fontFamily": "MyFont-Regular.ttf"
}
```

Set the font at the layout level (`fontFamily` on the root object) to apply
globally, or on individual slots (`fontFamily` on a slot) to override per-element.

### Bold Auto-Resolution

When `fontWeight` is `"bold"`, the renderer automatically looks for a bold
variant:
- If the font path contains `-Regular`, it is replaced with `-Bold`.
- Otherwise, `-Bold` is appended before the extension (e.g. `Foo.ttf` -> `Foo-Bold.ttf`).
- If the bold file does not exist, the regular file is used with synthetic
  bolding.

## Partial Display Updates

The renderer compares each new frame against the previous one pixel-by-pixel.
If nothing changed, no USB transfer occurs. If only a portion changed, only the
dirty region is encoded and sent as a positioned JPEG sub-image. If the dirty
region exceeds 60% of the display, a full frame is sent instead (JPEG overhead
makes partial updates less efficient for large changes).

This is fully automatic -- layout authors don't need to do anything.

## Live Reload

The device config file is monitored for changes. When you save changes to:
- The device config file (to switch layouts)
- The layout JSON file itself is re-read on layout switch

The display updates immediately with no restart required. The pixel cache is
cleared to force a full re-render.

## Creating a Custom Layout

1. **Start from a bundled layout.** Copy one of the bundled JSON files to
   your config directory as a starting point.

2. **Edit the JSON.** All properties use camelCase. The file is standard JSON
   (no comments allowed).

3. **Reference it in your device config.** Set `"layout": "my-layout-name"`
   (filename without `.json`).

4. **Iterate.** Changes to the device config trigger an immediate re-render.
   Adjust positions, colors, font sizes, etc. and save to see changes on the
   hardware display.

### Tips

- The display is 800x480. Design for 4 groups across (200px each) or 2x2 grid
  (400x240 each).
- Use `jpegQuality` 40-60 for a good balance of quality and USB transfer speed.
- Lower `fontSize` values work better on the small LCD (13-28px is typical).
- Use `opacity` to create layered effects (e.g. a dim decorative arc behind a
  bold gauge).
- Test with different group name lengths and member counts -- names vary widely.
- The `overflow: "truncate"` option is recommended for member lists to prevent
  long names from overflowing.

### Example: Minimal Layout

```json
{
  "width": 800,
  "height": 480,
  "background": "#000000",
  "slots": [
    {
      "repeat": "0-3",
      "repeatOffsetX": "200",
      "children": [
        {
          "type": "text",
          "x": "0", "y": "20", "w": "200",
          "text": "{group.name}",
          "color": "{group.color}",
          "fontSize": 24,
          "fontWeight": "bold",
          "align": "center"
        },
        {
          "type": "text",
          "x": "0", "y": "60", "w": "200",
          "text": "{group.volume}%",
          "color": "#FFFFFF",
          "fontSize": 48,
          "align": "center"
        }
      ]
    }
  ]
}
```

This creates a simple 4-column layout showing only the group name (in its
assigned color) and volume percentage for each dial.
