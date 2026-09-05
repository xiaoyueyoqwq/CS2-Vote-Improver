using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotVoteFix;

public enum HumanVoteOutcome
{
    Passed,
    FailedQuorum,
    FailedYesMustExceedNo,
    Cancelled,
}

/// <summary>
/// A yes/no vote that is displayed through the native Panorama vote HUD but
/// whose electorate and tally are owned by the plugin. Valve's CVoteController
/// is only used as the client-facing display state (the HUD reads
/// m_nPotentialVotes / m_nVoteOptionCount). Ballots come from the plugin's
/// Pre listener on "vote option1/option2"; vote_cast is a fallback if the
/// engine still fires it while m_iActiveIssueIndex != -1.
/// </summary>
public sealed class HumanVote
{
    // CCSUsrMsg_* ids: CS_UM_CallVoteFailed=45, VoteStart=46, VotePass=47, VoteFailed=48 (+300 in CS2).
    public const int UmCallVoteFailed = 345;
    public const int UmVoteStart = 346;
    public const int UmVotePass = 347;
    public const int UmVoteFailed = 348;

    private const int VoteUncast = -1;
    private const int OptionYes = 0;
    private const int OptionNo = 1;
    private const int MaxSlots = 64;

    /// <summary>
    /// Any non-negative index makes CVoteController::TryCastVote accept
    /// "vote optionN" and fire vote_cast without a native issue being active.
    /// Same trick as CS2Fixes' PanoramaVote.
    /// </summary>
    private const int FakeActiveIssueIndex = 2;

    private readonly HashSet<int> _voters;
    private readonly Dictionary<int, int> _ballots = new();
    private readonly CallVoteRequest _request;
    private readonly float _quorumRatio;
    private readonly Action<string> _log;

    public HumanVote(
        CallVoteRequest request,
        IReadOnlyCollection<int> voterSlots,
        float quorumRatio,
        Action<string> log)
    {
        _request = request;
        _voters = new HashSet<int>(voterSlots);
        _quorumRatio = quorumRatio;
        _log = log;
    }

    public CallVoteRequest Request => _request;
    public bool IsFinished { get; private set; }
    public int PotentialVotes => _voters.Count;
    public int YesVotes => _ballots.Count(kv => kv.Value == OptionYes);
    public int NoVotes => _ballots.Count(kv => kv.Value == OptionNo);

    public int RequiredYes =>
        Math.Max(1, (int)Math.Ceiling(PotentialVotes * _quorumRatio));

    public bool IsVoter(int slot) => _voters.Contains(slot);

    public void Start(CVoteController? controller)
    {
        if (controller != null && controller.IsValid)
        {
            ResetController(controller);
            controller.PotentialVotes = PotentialVotes;
            controller.IsYesNoVote = true;
            controller.OnlyTeamToVote = _request.IsTeamVote ? (int)_request.CallerTeam : -1;
            controller.ActiveIssueIndex = FakeActiveIssueIndex;
            Utilities.SetStateChanged(controller, "CVoteController", "m_nPotentialVotes");
            Utilities.SetStateChanged(controller, "CVoteController", "m_bIsYesNoVote");
            Utilities.SetStateChanged(controller, "CVoteController", "m_iOnlyTeamToVote");
            Utilities.SetStateChanged(controller, "CVoteController", "m_iActiveIssueIndex");
        }

        var recipients = BuildRecipients();
        var start = UserMessage.FromId(UmVoteStart);
        start.SetInt("team", _request.UserMessageTeam);
        start.SetInt("player_slot", _request.CallerSlot);
        start.SetInt("vote_type", -1);
        start.SetString("disp_str", _request.DisplayString);
        start.SetString("details_str", _request.DetailsForUi);
        start.SetBool("is_yes_no_vote", true);
        start.Send(recipients);

        BroadcastCounts(controller);
        _log($"vote start issue={_request.IssueType} details='{_request.Details}' caller={_request.CallerSlot} potential={PotentialVotes} required={RequiredYes} recipients={recipients.Count}");
    }

    /// <summary>
    /// Records a ballot from "vote optionN" or vote_cast. Returns false when
    /// the voter is not part of the human electorate (bots, HLTV, other team,
    /// late joiners) or the slot already voted.
    /// </summary>
    public bool TryCast(int slot, int option, CVoteController? controller)
    {
        if (IsFinished) return false;
        if (!_voters.Contains(slot))
        {
            _log($"ignored ballot slot={slot} option={option} (not in human voter pool)");
            return false;
        }
        if (option != OptionYes && option != OptionNo)
        {
            _log($"ignored ballot slot={slot} option={option} (not yes/no)");
            return false;
        }
        if (_ballots.ContainsKey(slot))
        {
            _log($"ignored duplicate ballot slot={slot}");
            return false;
        }

        _ballots[slot] = option;
        BroadcastCounts(controller);
        _log($"ballot slot={slot} option={(option == OptionYes ? "yes" : "no")} yes={YesVotes} no={NoVotes} potential={PotentialVotes} required={RequiredYes}");
        return true;
    }

    /// <summary>Drops a voter who disconnected mid-vote and shrinks the electorate.</summary>
    public void RemoveVoter(int slot, CVoteController? controller)
    {
        if (IsFinished) return;
        if (!_voters.Remove(slot)) return;
        _ballots.Remove(slot);
        if (controller != null && controller.IsValid)
        {
            controller.PotentialVotes = PotentialVotes;
            Utilities.SetStateChanged(controller, "CVoteController", "m_nPotentialVotes");
        }
        BroadcastCounts(controller);
        _log($"voter left slot={slot} potential={PotentialVotes} required={RequiredYes}");
    }

    /// <summary>Returns the outcome once it can no longer change, otherwise null.</summary>
    public HumanVoteOutcome? EvaluateEarly()
    {
        if (PotentialVotes == 0) return HumanVoteOutcome.FailedQuorum;
        if (YesVotes >= RequiredYes) return HumanVoteOutcome.Passed;
        int remaining = PotentialVotes - _ballots.Count;
        if (YesVotes + remaining < RequiredYes) return HumanVoteOutcome.FailedQuorum;
        return null;
    }

    public HumanVoteOutcome EvaluateAtTimeout()
    {
        if (YesVotes >= RequiredYes) return HumanVoteOutcome.Passed;
        if (YesVotes > 0 && YesVotes <= NoVotes) return HumanVoteOutcome.FailedYesMustExceedNo;
        return HumanVoteOutcome.FailedQuorum;
    }

    public void Finish(HumanVoteOutcome outcome, CVoteController? controller)
    {
        if (IsFinished) return;
        IsFinished = true;

        var recipients = BuildRecipients();
        if (outcome == HumanVoteOutcome.Passed)
        {
            var pass = UserMessage.FromId(UmVotePass);
            pass.SetInt("team", _request.UserMessageTeam);
            pass.SetInt("vote_type", -1);
            pass.SetString("disp_str", _request.Issue.PassedString);
            pass.SetString("details_str", _request.DetailsForUi);
            pass.Send(recipients);
        }
        else
        {
            var failed = UserMessage.FromId(UmVoteFailed);
            failed.SetInt("team", _request.UserMessageTeam);
            failed.SetInt("reason", outcome switch
            {
                HumanVoteOutcome.FailedYesMustExceedNo => VoteFailReason.YesMustExceedNo,
                HumanVoteOutcome.Cancelled => VoteFailReason.Generic,
                _ => VoteFailReason.Quorum,
            });
            failed.Send(recipients);
        }

        if (controller != null && controller.IsValid)
        {
            ResetController(controller);
        }

        _log($"vote end issue={_request.IssueType} outcome={outcome} yes={YesVotes} no={NoVotes} potential={PotentialVotes} required={RequiredYes}");
    }

    private RecipientFilter BuildRecipients()
    {
        var filter = new RecipientFilter();
        foreach (int slot in _voters)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player != null && player.IsValid) filter.Add(player);
        }
        return filter;
    }

    private void BroadcastCounts(CVoteController? controller)
    {
        int yes = YesVotes;
        int no = NoVotes;

        if (controller != null && controller.IsValid)
        {
            Span<int> counts = controller.VoteOptionCount;
            counts[OptionYes] = yes;
            counts[OptionNo] = no;
            for (int i = 2; i < counts.Length; i++) counts[i] = 0;

            Span<int> cast = controller.VotesCast;
            for (int slot = 0; slot < Math.Min(MaxSlots, cast.Length); slot++)
            {
                cast[slot] = _ballots.TryGetValue(slot, out int option) ? option : VoteUncast;
            }

            Utilities.SetStateChanged(controller, "CVoteController", "m_nVoteOptionCount");
            Utilities.SetStateChanged(controller, "CVoteController", "m_nVotesCast");
        }

        var changed = new EventVoteChanged(true)
        {
            VoteOption1 = yes,
            VoteOption2 = no,
            VoteOption3 = 0,
            VoteOption4 = 0,
            VoteOption5 = 0,
            Potentialvotes = PotentialVotes,
            Yesvotes = yes,
            Novotes = no,
        };
        changed.FireEvent(false);
    }

    private static void ResetController(CVoteController controller)
    {
        Span<int> cast = controller.VotesCast;
        for (int i = 0; i < cast.Length; i++) cast[i] = VoteUncast;

        Span<int> counts = controller.VoteOptionCount;
        for (int i = 0; i < counts.Length; i++) counts[i] = 0;

        controller.PotentialVotes = 0;
        controller.ActiveIssueIndex = -1;
        controller.OnlyTeamToVote = -1;
        Utilities.SetStateChanged(controller, "CVoteController", "m_nVotesCast");
        Utilities.SetStateChanged(controller, "CVoteController", "m_nVoteOptionCount");
        Utilities.SetStateChanged(controller, "CVoteController", "m_nPotentialVotes");
        Utilities.SetStateChanged(controller, "CVoteController", "m_iActiveIssueIndex");
        Utilities.SetStateChanged(controller, "CVoteController", "m_iOnlyTeamToVote");
    }
}
