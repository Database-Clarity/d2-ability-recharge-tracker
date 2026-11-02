using System.Drawing;

namespace D2AbilityTracker;

internal static class AppIcon
{
    private static Icon? _icon;

    public static Icon? Load()
    {
        if (_icon is not null)
        {
            return _icon;
        }

        try
        {
            _icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);
        }
        catch (Exception)
        {
            _icon = null;
        }

        return _icon;
    }
}
