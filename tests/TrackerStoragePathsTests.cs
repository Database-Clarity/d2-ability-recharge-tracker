using D2AbilityTracker;

internal static class TrackerStoragePathsTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"D2AbilityTracker-storage-tests-{Guid.NewGuid():N}");
        var executableDirectory = Path.Combine(root, "portable");
        var appDataDirectory = Path.Combine(root, "appdata");
        Directory.CreateDirectory(executableDirectory);
        try
        {
            var installed = TrackerStoragePaths.ForExecutableDirectory(executableDirectory, appDataDirectory);
            Ensure(!installed.IsPortable &&
                installed.SettingsPath == Path.Combine(appDataDirectory, "D2AbilityTracker", "settings.json") &&
                installed.MeasurementHistoryPath == Path.Combine(appDataDirectory, "D2AbilityTracker", "measurement-history.json"),
                "Installed builds should continue to store settings and measurement history in AppData.");

            File.WriteAllText(Path.Combine(executableDirectory, "portable.flag"), string.Empty);
            var portable = TrackerStoragePaths.ForExecutableDirectory(executableDirectory, appDataDirectory);
            Ensure(portable.IsPortable &&
                portable.SettingsPath == Path.Combine(executableDirectory, "settings.json") &&
                portable.MeasurementHistoryPath == Path.Combine(executableDirectory, "measurement-history.json") &&
                portable.UpdateSourceVersionPath == Path.Combine(executableDirectory, "update-source-version.txt"),
                "Portable builds should store all app data alongside the executable.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
