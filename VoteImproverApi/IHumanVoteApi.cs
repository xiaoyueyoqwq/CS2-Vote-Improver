// VoteImproverApi: public contract that Vote Improver exposes via
// CounterStrikeSharp's PluginCapability system.
// Key = "voteimprover:api".
//
// The electorate is always humans only (not HLTV, not engine IsBot,
// not botidentity:api.IsManagedBot). Callers do not supply a voter list.

namespace VoteImproverApi;

public enum HumanVoteOutcome
{
    Passed,
    FailedQuorum,
    FailedYesMustExceedNo,
    Cancelled,
}

public sealed class HumanVoteRequest
{
    /// <summary>Issue name stored in logs and cooldown keys, e.g. "Overtime".</summary>
    public required string IssueType { get; init; }

    /// <summary>Panorama token for VoteStart.disp_str.</summary>
    public string DisplayString { get; init; } = "#SFUI_vote";

    /// <summary>Panorama token for VotePass.disp_str.</summary>
    public string PassedString { get; init; } = "#SFUI_vote_passed";

    /// <summary>Second HUD line. Use this for a custom Chinese question.</summary>
    public string DetailsForUi { get; init; } = "";

    /// <summary>Vote length in seconds. 0 = Vote Improver / sv_vote_timer_duration default.</summary>
    public float DurationSeconds { get; init; }
}

public interface IHumanVoteApi
{
    bool IsVoteActive { get; }

    /// <summary>
    /// Starts a server-initiated yes/no vote on the native Panorama HUD.
    /// Returns false when the plugin is disabled, a vote is already running,
    /// or there are no human voters. On success the HUD is shown and
    /// <paramref name="onComplete"/> runs once with the outcome.
    /// An empty command is implied: pass does not execute a server command.
    /// </summary>
    bool TryStartVote(HumanVoteRequest request, Action<HumanVoteOutcome> onComplete);

    void CancelActiveVote(string reason);
}
