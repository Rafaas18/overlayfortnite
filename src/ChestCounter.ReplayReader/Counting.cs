using System.Text.RegularExpressions;

namespace ChestCounter;

/// <summary>
/// Applies the user's filter (which container classes are "chests") and count mode to parsed matches.
/// </summary>
public static class Counting
{
    public const string DefaultChestPattern = "Chest";

    public static Regex BuildFilter(string? pattern)
    {
        try
        {
            return new Regex(string.IsNullOrWhiteSpace(pattern) ? DefaultChestPattern : pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException)
        {
            return new Regex(DefaultChestPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    public static MatchCount Count(MatchResult match, Regex filter, CountMode mode)
    {
        int live = 0, already = 0, squad = 0, attributed = 0, nearSquad = 0, nearMe = 0;
        int mine = 0, team = 0, known = 0;

        foreach (var c in match.Classes)
        {
            if (!filter.IsMatch(c.ShortName))
            {
                continue;
            }

            live += c.Live;
            already += c.AlreadyOpen;
            squad += c.LiveBySquad;
            attributed += c.LiveAttributed;
            nearSquad += c.NearSquad;
            nearMe += c.NearMe;
            mine += c.Mine;
            team += c.Team;
            known += c.Attributed;
        }

        // The squad number is only trustworthy when most openings say who did them.
        var squadKnown = live > 0 && attributed * 2 >= live;

        var used = mode;
        if (mode == CountMode.Auto)
        {
            // Replays that say who opened each chest (every recent one) count only the player's own chests.
            used = known > 0 ? CountMode.Mine : CountMode.All;
        }

        var counted = used switch
        {
            CountMode.Mine => mine,
            CountMode.MineAndTeam => mine + team,
            CountMode.NearSquad => nearSquad,
            CountMode.NearMe => nearMe,
            CountMode.Squad => squad,
            CountMode.All => live + already,
            _ => live
        };

        return new MatchCount(counted, live, already, squad, squadKnown, nearSquad, nearMe, match.HasPositions, used, mine, team, known);
    }
}
