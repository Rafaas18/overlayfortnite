using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ChestCounter.Overlay;

public sealed record RecentRow(string When, string Note, string Counted);

/// <summary>
/// Text shown in the overlay, computed from the match history and the settings.
/// </summary>
public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private const int RecentRows = 5;

    private string _totalText = "0";
    private string _summaryText = "";
    private string _statusText = "";
    private string _liveText = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RecentRow> Recent { get; } = new();

    public string TotalText { get => _totalText; private set => Set(ref _totalText, value); }
    public string SummaryText { get => _summaryText; private set => Set(ref _summaryText, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string LiveText { get => _liveText; private set => Set(ref _liveText, value); }

    /// <summary>Sum of counted chests over the whole history, without the baseline.</summary>
    public int HistoryTotal { get; private set; }

    public void Update(IReadOnlyList<MatchResult> matches, Settings settings, DateTime sessionStartUtc)
    {
        var filter = Counting.BuildFilter(settings.ChestPattern);
        var total = 0;
        var session = 0;
        var rows = new List<RecentRow>();
        var live = false;

        // Matches come newest first.
        foreach (var match in matches)
        {
            if (match.Error is not null)
            {
                if (rows.Count < RecentRows)
                {
                    rows.Add(new RecentRow(FormatWhen(match), "no se pudo leer", "–"));
                }

                continue;
            }

            var count = Counting.Count(match, filter, settings.Mode);
            total += count.Counted;

            if (match.FileLastWriteUtc >= sessionStartUtc)
            {
                session += count.Counted;
            }

            live |= match.InProgress;

            if (rows.Count < RecentRows)
            {
                var note = match.InProgress ? "en curso" : $"{match.LengthMs / 60000} min";
                if (count.ModeUsed != CountMode.All && count.AlreadyOpen > 0)
                {
                    note += $" · {count.AlreadyOpen} ya abiertos";
                }

                rows.Add(new RecentRow(FormatWhen(match), note, count.Counted.ToString("N0")));
            }
        }

        HistoryTotal = total;
        TotalText = (settings.Baseline + total).ToString("N0");
        SummaryText = $"Esta sesión: {session:N0}   ·   Partidas: {matches.Count(m => m.Error is null):N0}";
        LiveText = live ? "● EN PARTIDA" : "";

        if (!Recent.SequenceEqual(rows))
        {
            Recent.Clear();
            foreach (var row in rows)
            {
                Recent.Add(row);
            }
        }
    }

    private static string FormatWhen(MatchResult match) => match.StartedUtc.ToLocalTime().ToString("dd/MM HH:mm");

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
