namespace D2AbilityTracker;

// On-demand automatic detection of ability slot regions.
internal sealed partial class MainForm
{
    /// <summary>Detects this ability's slot in the Destiny 2 window and saves it as its region.</summary>
    private void AutoDetectRegion(AbilityKind kind)
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
                var index = Array.IndexOf(AbilityKinds, kind);
                if (index < 0 || regions.Length != AbilityKinds.Length)
                {
                    failure = "Couldn't confidently find all three ability slots. Make sure the HUD is visible and you're not in a menu, then try again.";
                }
                else
                {
                    var slot = GetSlot(kind);
                    slot.Region = regions[index];
                    slot.MonitorBounds = target.MonitorBounds;
                    slot.MonitorDeviceName = target.DeviceName;
                    slot.GameWindowBounds = target.Bounds;
                    slot.RegionAutoDetected = true;
                    _trackers[kind].ResetCycle();
                    lock (_captureSync)
                    {
                        if (_monitoring)
                        {
                            RefreshCaptureRegions();
                        }
                    }

                    SaveSettings();
                    UpdateStatus();
                    UpdateOverlay();
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

        if (failure is not null && MessageBox.Show(
            this,
            $"{failure}\n\nWould you like to select the area manually instead?",
            Text,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information) == DialogResult.Yes)
        {
            SelectRegion(kind);
        }
    }
}
