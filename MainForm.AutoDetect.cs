namespace D2AbilityTracker;

// On-demand automatic detection of ability slot regions.
internal sealed partial class MainForm
{
    /// <summary>Detects this ability's slot in the Destiny 2 window and saves it as its region.</summary>
    private void AutoDetectRegion(AbilityKind kind)
    {
        MessageBox.Show(this, $"Auto-detect for {KindName(kind)} is not implemented yet.", Text);
    }
}
