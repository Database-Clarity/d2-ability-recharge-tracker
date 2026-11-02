namespace D2AbilityTracker;

// On-demand automatic detection of ability slot regions.
internal sealed partial class MainForm
{
    private void AutoDetectRegions()
    {
        if (_selectingRegion)
        {
            return;
        }

        var wasMonitoring = _monitoring;
        string? failure = null;
        _selectingRegion = true;
        SetMonitoring(false);
        try
        {
            Hide();
            _overlay.Hide();
            Thread.Sleep(150);

            var target = ScreenCapture.FindDestiny2Window();
            if (target is null)
            {
                failure = "Couldn't find the Destiny 2 window. Start the game and make sure it isn't minimized, then try again.";
            }
            else
            {
                using var snapshot = ScreenCapture.Capture(target.Bounds);
                var regions = AbilitySlotRegionFinder.Find(snapshot, target.Bounds);
                if (regions.Length != AbilityKinds.Length)
                {
                    failure = "Couldn't confidently find all three ability slots. Make sure the HUD is visible and you're not in a menu, then try again.";
                }
                else
                {
                    using var picker = new RegionPicker(snapshot, target.Bounds, target.MonitorBounds,
                        "the Destiny 2 window", initialSelections: regions);
                    if (picker.ShowDialog() == DialogResult.OK)
                    {
                        for (var index = 0; index < AbilityKinds.Length; index++)
                        {
                            var kind = AbilityKinds[index];
                            var slot = GetSlot(kind);
                            slot.Region = picker.SelectedRegions[index];
                            slot.MonitorBounds = target.MonitorBounds;
                            slot.MonitorDeviceName = target.DeviceName;
                            slot.GameWindowBounds = target.Bounds;
                            slot.RegionAutoDetected = true;
                            _trackers[kind].ResetCycle();
                        }
                        SaveSettings();
                        UpdateStatus();
                        UpdateOverlay();
                    }
                }
            }
        }
        catch (Exception exception)
        {
            failure = $"Couldn't auto-detect the ability slot: {exception.Message}";
        }
        finally
        {
            _selectingRegion = false;
            Show();
            Activate();
            ShowOverlayIfEnabled();
            UpdateOverlay();
            if (wasMonitoring)
            {
                SetMonitoring(true);
            }
        }

        if (failure is not null) MessageBox.Show(
            this,
            failure,
            Text,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
