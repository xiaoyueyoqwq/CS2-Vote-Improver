using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotVoteFix;

public enum VoteScope
{
    Global,
    Team,
}

/// <summary>
/// Reasons for CCSUsrMsg_CallVoteFailed / CCSUsrMsg_VoteFailed. Values follow
/// the CS:GO vote_create_failed_t enum, which the CS2 Panorama UI still maps
/// to #SFUI_vote_failed_* strings.
/// </summary>
public static class VoteFailReason
{
    public const int Generic = 0;
    public const int RateExceeded = 2;
    public const int YesMustExceedNo = 3;
    public const int Quorum = 4;
    public const int IssueDisabled = 5;
    public const int MapNotFound = 6;
    public const int OnCooldown = 8;
    public const int PlayerNotFound = 11;
    public const int Spectator = 14;
    public const int RecentKick = 15;
    public const int RecentChangeMap = 16;
    public const int RecentSwapTeams = 17;
    public const int RecentScrambleTeams = 18;
    public const int RecentRestart = 19;
}

public sealed class IssueConfig
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>"Global" = every human votes; "Team" = only the caller's team.</summary>
    [JsonPropertyName("Scope")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VoteScope Scope { get; set; } = VoteScope.Global;

    /// <summary>Panorama string shown while the vote runs (VoteStart.disp_str).</summary>
    [JsonPropertyName("DisplayString")]
    public string DisplayString { get; set; } = "";

    /// <summary>Panorama string shown when the vote passes (VotePass.disp_str).</summary>
    [JsonPropertyName("PassedString")]
    public string PassedString { get; set; } = "#SFUI_vote_passed";

    /// <summary>
    /// Server command executed on pass. Placeholders: {details} {map} {userid}
    /// {team} (2/3) {team_name} (terrorist/ct). Empty = never take over.
    /// </summary>
    [JsonPropertyName("Command")]
    public string Command { get; set; } = "";

    /// <summary>Boolean ConVar that must be enabled; otherwise pass through to native.</summary>
    [JsonPropertyName("AllowConVar")]
    public string AllowConVar { get; set; } = "";

    [JsonPropertyName("FailedRecentlyReason")]
    public int FailedRecentlyReason { get; set; } = VoteFailReason.OnCooldown;

    public static Dictionary<string, IssueConfig> Defaults() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ChangeLevel"] = new IssueConfig
        {
            DisplayString = "#SFUI_vote_changelevel",
            PassedString = "#SFUI_vote_passed_changelevel",
            Command = "changelevel {map}",
            AllowConVar = "sv_vote_issue_changelevel_allowed",
            FailedRecentlyReason = VoteFailReason.RecentChangeMap,
        },
        ["Kick"] = new IssueConfig
        {
            DisplayString = "#SFUI_vote_kick_player_other",
            PassedString = "#SFUI_vote_passed_kick_player",
            Command = "kickid {userid}",
            AllowConVar = "sv_vote_issue_kick_allowed",
            FailedRecentlyReason = VoteFailReason.RecentKick,
        },
        ["RestartGame"] = new IssueConfig
        {
            DisplayString = "#SFUI_vote_restart_game",
            PassedString = "#SFUI_vote_passed_restart_game",
            Command = "mp_restartgame 1",
            AllowConVar = "sv_vote_issue_restart_game_allowed",
            FailedRecentlyReason = VoteFailReason.RecentRestart,
        },
        ["StartTimeOut"] = new IssueConfig
        {
            Scope = VoteScope.Team,
            DisplayString = "#SFUI_vote_start_timeout",
            PassedString = "#SFUI_vote_passed_timeout",
            Command = "timeout_{team_name}_start",
            AllowConVar = "sv_vote_issue_timeout_allowed",
        },
        ["NextLevel"] = new IssueConfig
        {
            Enabled = false,
            DisplayString = "#SFUI_vote_nextlevel",
            PassedString = "#SFUI_vote_passed_nextlevel",
            Command = "nextlevel {map}",
            AllowConVar = "sv_vote_issue_nextlevel_allowed",
            FailedRecentlyReason = VoteFailReason.RecentChangeMap,
        },
        ["ScrambleTeams"] = new IssueConfig
        {
            Enabled = false,
            DisplayString = "#SFUI_vote_scramble_teams",
            PassedString = "#SFUI_vote_passed_scramble_teams",
            Command = "mp_scrambleteams",
            AllowConVar = "sv_vote_issue_scramble_teams_allowed",
            FailedRecentlyReason = VoteFailReason.RecentScrambleTeams,
        },
        ["SwapTeams"] = new IssueConfig
        {
            Enabled = false,
            DisplayString = "#SFUI_vote_swap_teams",
            PassedString = "#SFUI_vote_passed_swap_teams",
            Command = "mp_swapteams",
            AllowConVar = "sv_vote_issue_swap_teams_allowed",
            FailedRecentlyReason = VoteFailReason.RecentSwapTeams,
        },
        // No console command triggers a surrender; leave native handling.
        ["Surrender"] = new IssueConfig
        {
            Enabled = false,
            Scope = VoteScope.Team,
            DisplayString = "#SFUI_vote_surrender",
            PassedString = "#SFUI_vote_passed_surrender",
            Command = "",
            AllowConVar = "sv_vote_issue_surrrender_allowed",
        },
    };
}

/// <summary>
/// A parsed <c>callvote &lt;IssueType&gt; [details...]</c> client command.
/// </summary>
public sealed class CallVoteRequest
{
    public required string IssueType { get; init; }
    public required string Details { get; init; }
    public required IssueConfig Issue { get; init; }
    public required int CallerSlot { get; init; }
    public required CsTeam CallerTeam { get; init; }

    /// <summary>Kick target userid, when the issue is Kick.</summary>
    public int TargetUserId { get; init; } = -1;
    public int TargetSlot { get; init; } = -1;

    public bool IsTeamVote => Issue.Scope == VoteScope.Team;

    /// <summary>Team value written into VoteStart / VotePass (-1 = everyone).</summary>
    public int UserMessageTeam => IsTeamVote ? (int)CallerTeam : -1;

    /// <summary>Key used for the per-issue failure cooldown.</summary>
    public string CooldownKey => IsKick ? $"{IssueType}:{TargetUserId}" : $"{IssueType}:{Details}";

    public bool IsKick => string.Equals(IssueType, "Kick", StringComparison.OrdinalIgnoreCase);

    public string DisplayString
    {
        get
        {
            if (!IsKick || Issue.DisplayString != "#SFUI_vote_kick_player_other")
                return Issue.DisplayString;

            var reason = KickReason(Details);
            return reason switch
            {
                "cheating" => "#SFUI_vote_kick_player_cheating",
                "idle" => "#SFUI_vote_kick_player_idle",
                "scamming" => "#SFUI_vote_kick_player_scamming",
                _ => "#SFUI_vote_kick_player_other",
            };
        }
    }

    /// <summary>Second half of the Panorama string (map name or target name).</summary>
    public string DetailsForUi { get; init; } = "";

    public string BuildCommand()
    {
        var teamName = CallerTeam == CsTeam.CounterTerrorist ? "ct" : "terrorist";
        return Issue.Command
            .Replace("{details}", Details, StringComparison.Ordinal)
            .Replace("{map}", Details, StringComparison.Ordinal)
            .Replace("{userid}", TargetUserId.ToString(), StringComparison.Ordinal)
            .Replace("{team}", ((int)CallerTeam).ToString(), StringComparison.Ordinal)
            .Replace("{team_name}", teamName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Splits kick details into (userid, reason). Accepts "12", "12 cheating",
    /// and the quoted single-argument form "12 cheating".
    /// </summary>
    public static bool TryParseKickTarget(string details, out int userId)
    {
        userId = -1;
        var tokens = details.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 0 && int.TryParse(tokens[0], out userId) && userId >= 0;
    }

    public static string KickReason(string details)
    {
        var tokens = details.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 1 ? tokens[1].ToLowerInvariant() : "other";
    }
}
