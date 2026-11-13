using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChestCounter.Overlay;

/// <summary>Small "type a value" window.</summary>
internal static class InputDialog
{
    public static string? Ask(string title, string message, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 12), MinWidth = 320 };
        var ok = new Button { Content = "Aceptar", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, MinWidth = 80 };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ShowInTaskbar = true
        };

        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        return window.ShowDialog() == true ? box.Text : null;
    }
}

/// <summary>One line of the matches table. Property names are the column headers.</summary>
public sealed record Row(string Fecha, string Duracion, string Modo, string Contados, int EnVivo, string DelSquad, int YaAbiertos, string Estado);

/// <summary>List of every match in the history, plus the container classes seen (to tune the chest pattern).</summary>
internal static class DetailsWindow
{
    public static void Show(IReadOnlyList<MatchResult> matches, Settings settings)
    {
        var filter = Counting.BuildFilter(settings.ChestPattern);
        var rows = new List<Row>();
        var classes = new Dictionary<string, (int Live, int Already)>();

        foreach (var match in matches)
        {
            var when = match.StartedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            if (match.Error is not null)
            {
                rows.Add(new Row(when, "", "", "–", 0, "", 0, "Error: " + match.Error));
                continue;
            }

            var count = Counting.Count(match, filter, settings.Mode);
            rows.Add(new Row(
                when,
                $"{match.LengthMs / 60000} min",
                match.Playlist ?? "",
                count.Counted.ToString(),
                count.Live,
                count.SquadKnown ? count.Squad.ToString() : "sin dato",
                count.AlreadyOpen,
                match.InProgress ? "En curso" : ""));

            foreach (var c in match.Classes)
            {
                classes.TryGetValue(c.ShortName, out var sum);
                classes[c.ShortName] = (sum.Live + c.Live, sum.Already + c.AlreadyOpen);
            }
        }

        var text = new StringBuilder();
        text.AppendLine($"Tipos de contenedor vistos en los replays (patrón actual: \"{settings.ChestPattern}\"). Los marcados con * cuentan como cofre.");
        foreach (var (name, sum) in classes.OrderByDescending(c => c.Value.Live + c.Value.Already))
        {
            text.AppendLine($"{(filter.IsMatch(name) ? "*" : " ")} {name}  —  abiertos a la vista: {sum.Live}, ya abiertos: {sum.Already}");
        }

        var grid = new DataGrid
        {
            ItemsSource = rows,
            IsReadOnly = true,
            AutoGenerateColumns = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Margin = new Thickness(8)
        };

        var classBox = new TextBox
        {
            Text = text.ToString(),
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 160,
            Margin = new Thickness(8, 0, 8, 8)
        };

        var layout = new DockPanel();
        DockPanel.SetDock(classBox, Dock.Bottom);
        layout.Children.Add(classBox);
        layout.Children.Add(grid);

        new Window
        {
            Title = "Overlay Cofres – partidas",
            Content = layout,
            Width = 860,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true
        }.Show();
    }
}
