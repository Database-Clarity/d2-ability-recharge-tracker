namespace D2AbilityTracker;

internal sealed record TrackerStoragePaths(
    bool IsPortable,
    string SettingsPath,
    string MeasurementHistoryPath,
    string UpdateSourceVersionPath)
{
    internal static TrackerStoragePaths Current { get; } = ForExecutableDirectory(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    internal static TrackerStoragePaths ForExecutableDirectory(string executableDirectory, string appDataDirectory)
    {
        var isPortable = File.Exists(Path.Combine(executableDirectory, "portable.flag"));
        var storageDirectory = isPortable ? executableDirectory : Path.Combine(appDataDirectory, "D2AbilityTracker");
        return new TrackerStoragePaths(
            isPortable,
            Path.Combine(storageDirectory, "settings.json"),
            Path.Combine(storageDirectory, "measurement-history.json"),
            Path.Combine(storageDirectory, "update-source-version.txt"));
    }
}
