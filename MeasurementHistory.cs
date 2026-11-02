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
    Guid? RepeatTestId = null,
    Dictionary<string, string>? CustomValues = null);

/// <summary>A named group of measurements. Custom columns belong to the session that defines them.</summary>
internal sealed record MeasurementHistorySession(
    Guid Id,
    string Name,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? CustomColumns = null)
{
    [JsonIgnore]
    public IReadOnlyList<string> Columns => CustomColumns ?? [];
}

internal sealed class MeasurementHistoryStore
{
    // Version 1 kept one global custom-column list; version 2 stores columns per session.
    private const int LegacyGlobalColumnsVersion = 1;
    private const int CurrentVersion = 2;
    private static readonly string[] ReservedColumns =
    [
        "Ability", "Result", "Measured", "Measured ±", "Regression", "Regression ±", "Measured at", "Note", "Session",
        "Measured seconds", "Measurement uncertainty seconds", "Regression seconds",
        "Regression uncertainty seconds", "Cycles", "Regression cycles", "Repeat test ID"
    ];
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
            Migrate(document);
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
    /// <summary>The custom columns defined for one session (empty for unknown sessions).</summary>
    public IReadOnlyList<string> GetCustomColumns(Guid sessionId)
    {
        lock (_sync) return _sessions.FirstOrDefault(session => session.Id == sessionId)?.Columns.ToArray() ?? [];
    }

    public void AddCustomColumn(Guid sessionId, string name)
    {
        name = name.Trim();
        lock (_sync)
        {
            EnsureWritable();
            var index = FindSession(sessionId);
            var session = _sessions[index];
            if (!IsValidColumnName(name) || session.Columns.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a column name (1–80 characters) that is unique in this session and different from the built-in columns.", nameof(name));
            var sessions = _sessions.ToList();
            sessions[index] = session with { CustomColumns = [.. session.Columns, name] };
            Commit(sessions, _measurements, _activeSessionId);
        }
    }

    public void SaveCustomValue(Guid measurementId, string column, string value)
    {
        lock (_sync)
        {
            EnsureWritable();
            var index = _measurements.FindIndex(item => item.Id == measurementId);
            if (index < 0) throw new ArgumentException("The measurement no longer exists.", nameof(measurementId));
            var session = _sessions.FirstOrDefault(item => item.Id == _measurements[index].SessionId);
            if (session is null || !session.Columns.Contains(column))
                throw new ArgumentException("The custom column does not exist in this measurement's session.", nameof(column));
            var measurements = _measurements.ToList();
            var values = measurements[index].CustomValues is { } existing
                ? new Dictionary<string, string>(existing) : [];
            values[column] = value;
            measurements[index] = measurements[index] with { CustomValues = values };
            Commit(_sessions, measurements, _activeSessionId);
        }
    }

    public bool CanWrite => LoadError is null;

    public IReadOnlyList<MeasurementHistoryEntry> Measurements
    {
        get
        {
            lock (_sync)
            {
                return _measurements.Select(item => item with
                {
                    CustomValues = item.CustomValues is { } values ? new Dictionary<string, string>(values) : null
                }).ToArray();
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

            var entry = measurement with
            {
                // A session deleted while a repeat test was running leaves its later results unsorted.
                SessionId = sessionId is { } id && _sessions.Any(session => session.Id == id) ? id : null,
                CustomValues = measurement.CustomValues is { } values ? new Dictionary<string, string>(values) : null
            };
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

    /// <summary>Makes a session active, ending the current one and reopening the target if it had ended.</summary>
    public void SetActiveSession(Guid sessionId, DateTimeOffset now)
    {
        lock (_sync)
        {
            EnsureWritable();
            var index = FindSession(sessionId);
            if (_activeSessionId == sessionId) return;
            var sessions = _sessions.ToList();
            if (_activeSessionId is { } activeId && sessions.FindIndex(session => session.Id == activeId) is var activeIndex and >= 0)
                sessions[activeIndex] = sessions[activeIndex] with { EndedAt = Max(now, sessions[activeIndex].StartedAt) };
            sessions[index] = sessions[index] with { EndedAt = null };
            Commit(sessions, _measurements, sessionId);
        }
    }

    public void RenameSession(Guid sessionId, string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
            throw new ArgumentException("Enter a session name (1–80 characters).", nameof(name));
        lock (_sync)
        {
            EnsureWritable();
            var index = FindSession(sessionId);
            var sessions = _sessions.ToList();
            sessions[index] = sessions[index] with { Name = name };
            Commit(sessions, _measurements, _activeSessionId);
        }
    }

    /// <summary>
    /// Deletes a session and its column definitions. Its measurements become unsorted and keep their stored
    /// custom values, which reappear if they are added to a session with a column of the same name.
    /// </summary>
    public void DeleteSession(Guid sessionId)
    {
        lock (_sync)
        {
            EnsureWritable();
            var index = FindSession(sessionId);
            var sessions = _sessions.ToList();
            sessions.RemoveAt(index);
            var measurements = _measurements
                .Select(item => item.SessionId == sessionId ? item with { SessionId = null } : item).ToList();
            Commit(sessions, measurements, _activeSessionId == sessionId ? null : _activeSessionId);
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

    /// <summary>
    /// Moves a measurement to a session, or leaves it unsorted when <paramref name="sessionId"/> is null.
    /// Custom values are kept: those matching a column of the destination session show there, the rest stay
    /// stored (hidden) and reappear if the measurement joins a session with a column of that name.
    /// </summary>
    public void AssignToSession(Guid measurementId, Guid? sessionId)
    {
        lock (_sync)
        {
            EnsureWritable();
            if (sessionId is { } id) FindSession(id);
            var index = _measurements.FindIndex(measurement => measurement.Id == measurementId);
            if (index < 0)
            {
                throw new ArgumentException("The selected measurement no longer exists.", nameof(measurementId));
            }

            var measurements = _measurements.ToList();
            measurements[index] = measurements[index] with { SessionId = sessionId };
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

    private int FindSession(Guid sessionId)
    {
        var index = _sessions.FindIndex(session => session.Id == sessionId);
        return index >= 0 ? index : throw new ArgumentException("The selected session no longer exists.", nameof(sessionId));
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    private static bool IsValidColumnName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 80 && name == name.Trim() &&
        !ReservedColumns.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Upgrades a version 1 document in memory (the file is rewritten on the next change): each global column
    /// is copied onto every session with at least one measurement holding a value for it. Values on unsorted
    /// measurements stay stored on the measurement.
    /// </summary>
    private static void Migrate(MeasurementHistoryDocument document)
    {
        if (document.Version != LegacyGlobalColumnsVersion) return;
        var global = document.CustomColumns ?? [];
        document.Sessions = document.Sessions.Select(session =>
        {
            var used = global.Where(column => document.Measurements.Any(item =>
                item.SessionId == session.Id && item.CustomValues?.ContainsKey(column) == true)).ToArray();
            return session with { CustomColumns = used.Length > 0 ? used : null };
        }).ToList();
        document.CustomColumns = null;
        document.Version = CurrentVersion;
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
        var legacy = document.Version == LegacyGlobalColumnsVersion;
        if (document.Version is not (LegacyGlobalColumnsVersion or CurrentVersion) ||
            document.Sessions is null || document.Measurements is null ||
            (legacy ? document.CustomColumns is null || !AreValidColumns(document.CustomColumns)
                : document.CustomColumns is not null))
        {
            throw new InvalidDataException("The history file has an unsupported version or missing data.");
        }

        var sessionIds = new HashSet<Guid>();
        foreach (var session in document.Sessions)
        {
            if (session is null || session.Id == Guid.Empty || !sessionIds.Add(session.Id) ||
                string.IsNullOrWhiteSpace(session.Name) ||
                session.EndedAt is { } endedAt && endedAt < session.StartedAt ||
                session.CustomColumns is { } columns && (legacy || !AreValidColumns(columns)))
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
            // Values may outlive their column (after a move or a deleted session) and stay stored, hidden.
            if (measurement.CustomValues is { } values && values.Any(pair =>
                (legacy ? !document.CustomColumns!.Contains(pair.Key) : string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 80) ||
                pair.Value is null))
                throw new InvalidDataException("A measurement refers to an invalid custom column.");
            if (!measurementIds.Add(measurement.Id) ||
                measurement.SessionId is { } sessionId && !sessionIds.Contains(sessionId))
            {
                throw new InvalidDataException("The history file contains a duplicate measurement or an invalid session reference.");
            }
        }
    }

    private static bool AreValidColumns(IReadOnlyCollection<string> columns) =>
        columns.All(name => name is not null && IsValidColumnName(name)) &&
        columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() == columns.Count;

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
        /// <summary>Version 1 only: columns shared by all sessions. Absent from current documents.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? CustomColumns { get; set; }
    }
}
