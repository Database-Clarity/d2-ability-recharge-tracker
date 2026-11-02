using System.Drawing;

namespace D2AbilityTracker;

/// <summary>
/// Test hook: when enabled, app windows open off-screen (or fully transparent where their geometry
/// matters), never take activation and stay out of the taskbar. DrawToBitmap rendering is unaffected.
/// </summary>
internal static class HeadlessUi
{
    internal const int WsExToolWindow = 0x00000080;
    internal const int WsExNoActivate = 0x08000000;
    internal static readonly Point OffscreenLocation = new(-32000, -32000);

    internal static bool Enabled { get; set; }

    internal static CreateParams Apply(CreateParams parameters)
    {
        // Drop WS_EX_APPWINDOW so no taskbar button appears even for ShowInTaskbar windows.
        if (Enabled) parameters.ExStyle = (parameters.ExStyle & ~0x00040000) | WsExToolWindow | WsExNoActivate;
        return parameters;
    }
}
