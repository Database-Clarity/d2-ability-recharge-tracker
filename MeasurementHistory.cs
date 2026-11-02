using System.Text.Json;
using System.Text.Json.Serialization;

namespace D2AbilityTracker;

internal sealed record MeasurementHistoryEntry(
    Guid Id,
    AbilityKind Ability,
    double DurationSeconds,
    double? RegressionSeconds,
    double UncertaintySeconds,
    double? RegressionUncertaintySeconds,
    MeasurementSource Source,
    DateTimeOffset MeasuredAt,
    string? Note,
    Guid? SessionId,
    int CycleCount = 1,
    int? RegressionCycleCount = null,
    Guid? RepeatTestId = null);

internal sealed record MeasurementHistorySession(
    Guid Id,
    string Name,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

internal sealed class MeasurementHistoryStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly object _sync = new();
    private readonly string _path;
    private List<MeasurementHistoryEntry> _measurements = [];
    private List<MeasurementHistorySession> _sessions = [];
    private Guid? _activeSessionId;

    public MeasurementHistoryStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "D2AbilityTracker",
            "measurement-history.json");

        try
        {
            using var stream = File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<MeasurementHistoryDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("The history file is empty.");
            Validate(document);
            _measurements = document.Measurements;
            _sessions = document.Sessions;
            _activeSessionId = document.ActiveSessionId;
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            LoadError = exception.Message;
        }
    }

    public string? LoadError { get; }
    public bool CanWrite => LoadError is null;

    public IReadOnlyList<MeasurementHistoryEntry> Measurements
    {
        get
        {
            lock (_sync)
            {
                return _measurements.ToArray();
            }
        }
    }

    public IReadOnlyList<MeasurementHistorySession> Sessions
    {
        get
        {
            lock (_sync)
            {
                return _sessions.ToArray();
            }
        }
    }

    public Guid? ActiveSessionId
    {
        get
        {
            lock (_sync)
            {
                return _activeSessionId;
            }
        }
    }

    public bool TryAddMeasurement(MeasurementHistoryEntry measurement)
    {
        lock (_sync) return TryAddMeasurement(measurement, _activeSessionId);
    }

    public bool TryAddMeasurement(MeasurementHistoryEntry measurement, Guid? sessionId)
    {
        ValidateMeasurement(measurement);
        lock (_sync)
        {
            EnsureWritable();
            if (_measurements.Any(existing =>
                existing.Id == measurement.Id ||
                (existing.Id == existing.RepeatTestId) == (measurement.Id == measurement.RepeatTestId) &&
                existing.CycleCount == measurement.CycleCount &&
                existing.RepeatTestId == measurement.RepeatTestId &&
                existing.Ability == measurement.Ability &&
                existing.MeasuredAt == measurement.MeasuredAt &&
                existing.DurationSeconds == measurement.DurationSeconds &&
                existing.Source == measurement.Source))
            {
                return false;
            }

            var entry = measurement with { SessionId = sessionId };
            Commit(_sessions, [.. _measurements, entry], _activeSessionId);
            return true;
        }
    }

    public MeasurementHistorySession CreateSession(string name, DateTimeOffset startedAt)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Enter a session name.", nameof(name));
        }

        lock (_sync)
        {
            EnsureWritable();
            var sessions = _sessions.ToList();
            if (_activeSessionId is { } activeId)
            {
                var activeIndex = sessions.FindIndex(session => session.Id == activeId);
                if (activeIndex >= 0)
                {
                    sessions[activeIndex] = sessions[activeIndex] with { EndedAt = startedAt };
                }
            }

            var created = new MeasurementHistorySession(Guid.NewGuid(), name, startedAt, null);
            sessions.Add(created);
            Commit(sessions, _measurements, created.Id);
            return created;
        }
    }

    public bool EndActiveSession(DateTimeOffset endedAt)
    {
        lock (_sync)
        {
            EnsureWritable();
            if (_activeSessionId is not { } activeId)
            {
                return false;
            }

            var sessions = _sessions.ToList();
            var activeIndex = sessions.FindIndex(session => session.Id == activeId);
            if (activeIndex < 0)
            {
                throw new InvalidDataException("The active session is missing from measurement history.");
            }

            sessions[activeIndex] = sessions[activeIndex] with { EndedAt = endedAt };
            Commit(sessions, _measurements, null);
            return true;
        }
    }

    public void SetActiveSession(Guid sessionId)
    {
        lock (_sync)
        {
            EnsureWritable();
            var session = _sessions.FirstOrDefault(candidate => candidate.Id == sessionId)
                ?? throw new ArgumentException("The selected session does not exist.", nameof(sessionId));
            if (session.EndedAt is not null)
            {
                throw new InvalidOperationException("An ended session cannot be made active.");
            }

            Commit(_sessions, _measurements, sessionId);
        }
    }

    public void SaveNote(Guid measurementId, string? note)
    {
        lock (_sync)
        {
            EnsureWritable();
            var index = _measurements.FindIndex(measurement => measurement.Id == measurementId);
            if (index < 0)
            {
                throw new ArgumentException("The selected measurement no longer exists.", nameof(measurementId));
            }

            var measurements = _measurements.ToList();
            measurements[index] = measurements[index] with { Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() };
            Commit(_sessions, measurements, _activeSessionId);
        }
    }

    public void AssignToActiveSession(Guid measurementId)
    {
        lock (_sync)
        {
            EnsureWritable();
            if (_activeSessionId is not { } activeSessionId)
            {
                throw new InvalidOperationException("Start or select an active session before assigning a measurement.");
            }

            var index = _measurements.FindIndex(measurement => measurement.Id == measurementId);
            if (index < 0)
            {
                throw new ArgumentException("The selected measurement no longer exists.", nameof(measurementId));
            }

            var measurements = _measurements.ToList();
            measurements[index] = measurements[index] with { SessionId = activeSessionId };
            Commit(_sessions, measurements, _activeSessionId);
        }
    }

    public static bool TryCreateValidatedMeasurement(
        AbilityKind ability,
        AbilityState state,
        TimeSpan? duration,
        TimeSpan? uncertainty,
        MeasurementSource? source,
        bool discarded,
        string? trackingWarning,
        DateTime timestamp,
        TimeSpan? regressionDuration,
        TimeSpan? regressionUncertainty,
        out MeasurementHistoryEntry? measurement)
    {
        measurement = null;
        if (state != AbilityState.Complete ||
            duration is not { } measured || !IsPositiveFinite(measured) ||
            uncertainty is not { } spread || !IsNonNegativeFinite(spread) ||
            source != MeasurementSource.Event ||
            discarded || trackingWarning is not null ||
            !Enum.IsDefined(ability) || timestamp == DateTime.MinValue || timestamp == DateTime.MaxValue)
        {
            return false;
        }

        double? regressionSeconds = null;
        double? regressionSpreadSeconds = null;
        if (regressionDuration is { } regression && IsPositiveFinite(regression) &&
            regressionUncertainty is { } regressionSpread && IsNonNegativeFinite(regressionSpread))
        {
            regressionSeconds = regression.TotalSeconds;
            regressionSpreadSeconds = regressionSpread.TotalSeconds;
        }

        measurement = new MeasurementHistoryEntry(
            Guid.NewGuid(),
            ability,
            measured.TotalSeconds,
            regressionSeconds,
            spread.TotalSeconds,
            regressionSpreadSeconds,
            MeasurementSource.Event,
            new DateTimeOffset(timestamp.ToUniversalTime()),
            null,
            null);
        return true;
    }

    private void EnsureWritable()
    {
        if (LoadError is { } error)
        {
            throw new InvalidOperationException($"Measurement history could not be loaded and was left unchanged: {error}");
        }
    }

    private void Commit(
        List<MeasurementHistorySession> sessions,
        List<MeasurementHistoryEntry> measurements,
        Guid? activeSessionId)
    {
        var document = new MeasurementHistoryDocument
        {
            Version = CurrentVersion,
            Sessions = sessions,
            Measurements = measurements,
            ActiveSessionId = activeSessionId
        };
        Validate(document);
        WriteAtomically(document);
        _sessions = sessions;
        _measurements = measurements;
        _activeSessionId = activeSessionId;
    }

    private void WriteAtomically(MeasurementHistoryDocument document)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The measurement history path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void Validate(MeasurementHistoryDocument document)
    {
        if (document.Version != CurrentVersion || document.Sessions is null || document.Measurements is null)
        {
            throw new InvalidDataException("The history file has an unsupported version or missing data.");
        }

        var sessionIds = new HashSet<Guid>();
        foreach (var session in document.Sessions)
        {
            if (session is null || session.Id == Guid.Empty || !sessionIds.Add(session.Id) ||
                string.IsNullOrWhiteSpace(session.Name) ||
                session.EndedAt is { } endedAt && endedAt < session.StartedAt)
            {
                throw new InvalidDataException("The history file contains an invalid session.");
            }
        }

        if (document.ActiveSessionId is { } activeId &&
            document.Sessions.All(session => session.Id != activeId || session.EndedAt is not null))
        {
            throw new InvalidDataException("The history file refers to a missing or ended active session.");
        }

        var measurementIds = new HashSet<Guid>();
        foreach (var measurement in document.Measurements)
        {
            ValidateMeasurement(measurement);
            if (!measurementIds.Add(measurement.Id) ||
                measurement.SessionId is { } sessionId && !sessionIds.Contains(sessionId))
            {
                throw new InvalidDataException("The history file contains a duplicate measurement or an invalid session reference.");
            }
        }
    }

    private static void ValidateMeasurement(MeasurementHistoryEntry measurement)
    {
        if (measurement is null || measurement.Id == Guid.Empty ||
            measurement.CycleCount is < 1 or > 100 ||
            measurement.RegressionCycleCount is { } count && (count < 0 || count > measurement.CycleCount) ||
            measurement.RepeatTestId == Guid.Empty ||
            measurement.CycleCount > 1 && (measurement.RepeatTestId is null || measurement.RegressionCycleCount is null) ||
            measurement.Id == measurement.RepeatTestId &&
                (measurement.RegressionCycleCount is null ||
                 (measurement.RegressionSeconds is null) != (measurement.RegressionCycleCount == 0)) ||
            !Enum.IsDefined(measurement.Ability) ||
            measurement.Source != MeasurementSource.Event ||
            !double.IsFinite(measurement.DurationSeconds) || measurement.DurationSeconds <= 0 ||
            !double.IsFinite(measurement.UncertaintySeconds) || measurement.UncertaintySeconds < 0 ||
            measurement.RegressionSeconds is { } regression && (!double.IsFinite(regression) || regression <= 0) ||
            measurement.RegressionUncertaintySeconds is { } regressionSpread &&
                (!double.IsFinite(regressionSpread) || regressionSpread < 0))
        {
            throw new InvalidDataException("The history file contains an invalid measurement.");
        }
    }

    private static bool IsPositiveFinite(TimeSpan value) =>
        double.IsFinite(value.TotalSeconds) && value > TimeSpan.Zero;

    private static bool IsNonNegativeFinite(TimeSpan value) =>
        double.IsFinite(value.TotalSeconds) && value >= TimeSpan.Zero;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class MeasurementHistoryDocument
    {
        public MeasurementHistoryDocument()
        {
        }

        public int Version { get; set; }
        public List<MeasurementHistorySession> Sessions { get; set; } = [];
        public List<MeasurementHistoryEntry> Measurements { get; set; } = [];
        public Guid? ActiveSessionId { get; set; }
    }
}
