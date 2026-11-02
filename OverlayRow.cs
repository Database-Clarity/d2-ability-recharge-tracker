using System.Drawing;

namespace D2AbilityTracker;

/// <summary>One ability shown on the overlay: a fixed label column plus one or two value lines.</summary>
/// <param name="Label">Short ability name drawn in the label column.</param>
/// <param name="Line1">Primary status text, drawn in the value column.</param>
/// <param name="Line2">Secondary text; ignored in one-line mode, reserved (blank) when null in two-line mode.</param>
/// <param name="StatusColor">Colour of the status dot; null draws a neutral dot.</param>
/// <param name="Progress">Recharge fill (0–1) drawn as a thin bar under the first line; null draws none.</param>
internal sealed record OverlayRow(string Label, string Line1, string? Line2 = null, Color? StatusColor = null, float? Progress = null);
