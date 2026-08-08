using System.Collections.ObjectModel;
using System.Globalization;
using EQLDamageMeter.Models;
using EQLDamageMeter.Services;

namespace EQLDamageMeter.ViewModels;

public sealed class SessionHistoryViewModel : ObservableObject
{
    private SessionEntryViewModel? _selectedSession;
    private bool _suppressSelectionSideEffects;

    public ObservableCollection<SessionEntryViewModel> Sessions { get; } = [];

    public SessionEntryViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (!SetProperty(ref _selectedSession, value)) return;
            if (_suppressSelectionSideEffects) return;
            // Keep current-session selection sticky when list rebuilds.
        }
    }

    public void LoadHistory(IEnumerable<SessionRecord> records, SessionRecord? current)
    {
        var selectedId = SelectedSession?.Id;
        _suppressSelectionSideEffects = true;
        try
        {
            Sessions.Clear();
            if (current is not null)
                Sessions.Add(SessionEntryViewModel.FromRecord(current, isLive: true));

            foreach (var record in records
                         .Where(item => current is null ||
                                        !string.Equals(item.Id, current.Id, StringComparison.Ordinal))
                         .OrderByDescending(item => item.StartedAt))
            {
                Sessions.Add(SessionEntryViewModel.FromRecord(record, isLive: false));
            }

            SelectedSession = Sessions.FirstOrDefault(item => item.Id == selectedId)
                              ?? Sessions.FirstOrDefault(item => item.IsLive)
                              ?? Sessions.FirstOrDefault();
        }
        finally
        {
            _suppressSelectionSideEffects = false;
        }
    }

    public void UpsertCurrent(SessionRecord current)
    {
        var existing = Sessions.FirstOrDefault(item => item.IsLive);
        if (existing is null)
        {
            existing = SessionEntryViewModel.FromRecord(current, isLive: true);
            Sessions.Insert(0, existing);
            SelectedSession ??= existing;
            return;
        }

        existing.Apply(current, isLive: true);
        if (SelectedSession is null || SelectedSession.IsLive)
            SelectedSession = existing;
    }
}

public sealed class SessionEntryViewModel : ObservableObject
{
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _characterText = string.Empty;
    private string _levelXpText = "0.0%";
    private string _levelsText = "0";
    private string _aaPointsText = "0";
    private string _motesText = "0";
    private string _durationText = "—";
    private bool _isLive;
    private IReadOnlyList<MoteCountRow> _moteRows = [];

    public string Id { get; private set; } = string.Empty;

    public bool IsLive
    {
        get => _isLive;
        private set => SetProperty(ref _isLive, value);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    public string CharacterText
    {
        get => _characterText;
        private set => SetProperty(ref _characterText, value);
    }

    public string LevelXpText
    {
        get => _levelXpText;
        private set => SetProperty(ref _levelXpText, value);
    }

    public string LevelsText
    {
        get => _levelsText;
        private set => SetProperty(ref _levelsText, value);
    }

    public string AaPointsText
    {
        get => _aaPointsText;
        private set => SetProperty(ref _aaPointsText, value);
    }

    public string MotesText
    {
        get => _motesText;
        private set => SetProperty(ref _motesText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    public IReadOnlyList<MoteCountRow> MoteRows
    {
        get => _moteRows;
        private set => SetProperty(ref _moteRows, value);
    }

    public static SessionEntryViewModel FromRecord(SessionRecord record, bool isLive)
    {
        var entry = new SessionEntryViewModel();
        entry.Apply(record, isLive);
        return entry;
    }

    public void Apply(SessionRecord record, bool isLive)
    {
        Id = record.Id;
        IsLive = isLive;
        Title = isLive ? "Current session" : FormatTitle(record.StartedAt);
        Subtitle = FormatSubtitle(record, isLive);
        CharacterText = string.IsNullOrWhiteSpace(record.Character)
            ? "—"
            : $"{record.Character} · {record.Server}";
        LevelXpText = $"{record.LevelXpPercent.ToString("0.0", CultureInfo.CurrentCulture)}%";
        LevelsText = record.StartLevel is { } start && record.EndLevel is { } end
            ? $"+{record.LevelsGained}  ({start} → {end})"
            : $"+{record.LevelsGained}";
        AaPointsText = $"+{record.AaPointsGained}";
        MotesText = record.MotesLooted.ToString(CultureInfo.CurrentCulture);
        DurationText = FormatDuration(record, isLive);
        MoteRows = record.MotesByName
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new MoteCountRow(pair.Key, pair.Value))
            .ToArray();
    }

    private static string FormatTitle(DateTime startedAt) =>
        startedAt.ToLocalTime().ToString("ddd MMM d · h:mm tt", CultureInfo.CurrentCulture);

    private static string FormatSubtitle(SessionRecord record, bool isLive)
    {
        var start = record.StartedAt.ToLocalTime();
        if (isLive || record.EndedAt is null)
            return $"Started {start.ToString("t", CultureInfo.CurrentCulture)} · {FormatDuration(record, isLive)}";

        var end = record.EndedAt.Value.ToLocalTime();
        return $"{start.ToString("t", CultureInfo.CurrentCulture)} – {end.ToString("t", CultureInfo.CurrentCulture)} · {FormatDuration(record, false)}";
    }

    private static string FormatDuration(SessionRecord record, bool isLive)
    {
        var end = isLive || record.EndedAt is null ? DateTime.Now : record.EndedAt.Value;
        var span = end - record.StartedAt;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1)
            return $"{span.Minutes}m {span.Seconds}s";
        return $"{Math.Max(1, (int)span.TotalSeconds)}s";
    }
}

public sealed record MoteCountRow(string Name, int Count);
