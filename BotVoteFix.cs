using System.Text.Json.Serialization;
using BotIdentityApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using CssTimer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace BotVoteFix;

public sealed class BotVoteFixConfig : BasePluginConfig
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("RefreshIntervalSeconds")]
    public float RefreshIntervalSeconds { get; set; } = 0.10f;

    [JsonPropertyName("MinimumPotentialVotes")]
    public int MinimumPotentialVotes { get; set; } = 1;
}

[MinimumApiVersion(334)]
public sealed class BotVoteFix : BasePlugin, IPluginConfig<BotVoteFixConfig>
{
    private const int UncastVote = 5;

    private CssTimer? _refreshTimer;
    private IBotIdentityApi? _botIdentityApi;
    private bool _voteActive;
    private bool _voteArmLogged;
    private int _lastPotentialVotes = -1;
    private readonly int[] _lastOptionCounts = new int[5];

    /// <summary>
    /// Snapshot of managed bot slots captured at vote start. Used to
    /// validate per-slot incarnation so a slot reuse during the vote
    /// (player disconnect, manual bot_kick) doesn't accidentally
    /// include a fresh human voter.
    /// </summary>
    private Dictionary<int, ulong>? _managedBotIncarnations;

    public override string ModuleName => "Bot Vote Fix";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "CS2-Vote-Improver";
    public override string ModuleDescription =>
        "Excludes managed bots (via the botidentity:api capability) from native vote quorum.";

    public BotVoteFixConfig Config { get; set; } = new();

    public override void Load(bool hotReload)
    {
        Logger.LogInformation("[VoteFix] Loading Bot Vote Fix v{Version}. Enabled={Enabled}",
            ModuleVersion, Config.Enabled);
        try
        {
            RegisterEventHandler<EventVoteOptions>(OnVoteOptions);
            RegisterEventHandler<EventVoteStarted>(OnVoteStarted);
            RegisterEventHandler<EventVoteCast>(OnVoteCast);
            RegisterEventHandler<EventVoteEnded>(OnVoteFinished);
            RegisterEventHandler<EventVotePassed>(OnVoteFinished);
            RegisterEventHandler<EventVoteFailed>(OnVoteFinished);
            Logger.LogInformation("[VoteFix] Event handlers registered: VoteOptions, VoteStarted, VoteCast, VoteEnded, VotePassed, VoteFailed");
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "[VoteFix] Event handler registration failed; plugin will not be usable");
            throw;
        }
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try { _botIdentityApi = CapabilityToken.Get(); }
        catch { _botIdentityApi = null; }
        Logger.LogInformation("botidentity:api {State}; managed bots will {Treatment}.",
            _botIdentityApi == null ? "not available" : "available",
            _botIdentityApi == null ? "fall back to engine IsBot" : "be excluded explicitly");
    }

    public override void Unload(bool hotReload)
    {
        _refreshTimer?.Kill();
        _refreshTimer = null;
        DeregisterEventHandler<EventVoteOptions>(OnVoteOptions);
        DeregisterEventHandler<EventVoteStarted>(OnVoteStarted);
        DeregisterEventHandler<EventVoteCast>(OnVoteCast);
        DeregisterEventHandler<EventVoteEnded>(OnVoteFinished);
        DeregisterEventHandler<EventVotePassed>(OnVoteFinished);
        DeregisterEventHandler<EventVoteFailed>(OnVoteFinished);
    }

    public void OnConfigParsed(BotVoteFixConfig config)
    {
        config.RefreshIntervalSeconds = Math.Clamp(config.RefreshIntervalSeconds, 0.05f, 1.0f);
        config.MinimumPotentialVotes = Math.Max(config.MinimumPotentialVotes, 0);
        Config = config;
    }

    // Capability token. CounterStrikeSharp disambiguates the same token
    // string across plugins; we use the API's full type name to match
    // the publisher registration in CS2-Bot-Identity.
    private static readonly PluginCapability<IBotIdentityApi> CapabilityToken = new("botidentity:api");

    private HookResult OnVoteOptions(EventVoteOptions @event, GameEventInfo info)
    {
        ArmVote("vote_options");
        RefreshVote();
        return HookResult.Continue;
    }

    private HookResult OnVoteStarted(EventVoteStarted @event, GameEventInfo info)
    {
        ArmVote("vote_started");
        return HookResult.Continue;
    }

    private HookResult OnVoteCast(EventVoteCast @event, GameEventInfo info)
    {
        if (!_voteActive)
            ArmVote("vote_cast");
        return HookResult.Continue;
    }

    private void ArmVote(string source)
    {
        if (!Config.Enabled)
        {
            if (!_voteArmLogged)
            {
                Logger.LogInformation("[VoteFix] vote trigger received from {Source}, but the fix is disabled", source);
                _voteArmLogged = true;
            }
            return;
        }

        if (_voteActive)
            return;

        _voteActive = true;
        _voteArmLogged = true;
        _lastPotentialVotes = -1;
        Array.Fill(_lastOptionCounts, -1);

        Logger.LogInformation("[VoteFix] Native vote tracking armed by {Source}; preparing managed-bot snapshot", source);

        // Capture the managed-bot slot→incarnation map at vote start.
        // If a slot is reused by a real human during the vote, the
        // incarnation changes and the slot is no longer treated as
        // managed.
        if (_botIdentityApi != null)
        {
            var snapshots = _botIdentityApi.GetManagedBotSnapshots();
            _managedBotIncarnations = new Dictionary<int, ulong>();
            foreach (var snapshot in snapshots)
            {
                _managedBotIncarnations[snapshot.Slot] = snapshot.Incarnation;
            }
            Logger.LogInformation("[VoteFix] Cached {Count} managed bot snapshots for vote", snapshots.Length);
        }
        else
        {
            Logger.LogInformation("[VoteFix] botidentity:api not available at vote start; using engine IsBot only");
        }

        _refreshTimer?.Kill();
        _refreshTimer = AddTimer(Config.RefreshIntervalSeconds, RefreshVote, TimerFlags.REPEAT);
        Logger.LogInformation("[VoteFix] Starting refresh timer with interval {Interval}s", Config.RefreshIntervalSeconds);

        AddTimer(0.0f, RefreshVote);
    }

    private HookResult OnVoteFinished(GameEvent @event, GameEventInfo info)
    {
        StopRefreshing();
        return HookResult.Continue;
    }

    private void StopRefreshing()
    {
        _voteActive = false;
        _refreshTimer?.Kill();
        _refreshTimer = null;
        _lastPotentialVotes = -1;
        Array.Fill(_lastOptionCounts, -1);
        _managedBotIncarnations = null;
        _voteArmLogged = false;
    }

    private void RefreshVote()
    {
        if (!Config.Enabled || !_voteActive)
            return;

        var controller = Utilities
            .FindAllEntitiesByDesignerName<CVoteController>("vote_controller")
            .LastOrDefault(entity => entity.IsValid && entity.ActiveIssueIndex >= 0);

        if (controller == null)
        {
            Logger.LogInformation("[VoteFix] RefreshVote: controller not found");
            return;
        }

        var maxSlots = controller.VotesCast.Length;
        var allPlayers = Utilities.GetPlayers().Where(p => p.IsValid).ToList();
        Logger.LogInformation("[VoteFix] RefreshVote: {PlayerCount} players, {CacheCount} cached bots",
            allPlayers.Count, _managedBotIncarnations?.Count ?? 0);

        var eligibleSlots = allPlayers
            .Where(player => IsEligibleVoter(player, _botIdentityApi, _managedBotIncarnations))
            .Select(player => player.Slot)
            .Where(slot => slot >= 0 && slot < maxSlots)
            .ToHashSet();

        Logger.LogInformation("[VoteFix] Eligible voters: {Count}/{Total}",
            eligibleSlots.Count, allPlayers.Count);

        var potentialVotes = Math.Max(eligibleSlots.Count, Config.MinimumPotentialVotes);
        var optionCounts = new int[5];
        var votesCastChanged = false;

        for (var slot = 0; slot < maxSlots; slot++)
        {
            var cast = controller.VotesCast[slot];
            if (!eligibleSlots.Contains(slot))
            {
                if (cast != UncastVote)
                {
                    controller.VotesCast[slot] = UncastVote;
                    votesCastChanged = true;
                }
                continue;
            }

            if (cast is >= 0 and < 5)
                optionCounts[cast]++;
            else if (cast != UncastVote)
            {
                controller.VotesCast[slot] = UncastVote;
                votesCastChanged = true;
            }
        }

        var changed = controller.PotentialVotes != potentialVotes;
        controller.PotentialVotes = potentialVotes;

        for (var option = 0; option < optionCounts.Length; option++)
        {
            changed |= controller.VoteOptionCount[option] != optionCounts[option];
            controller.VoteOptionCount[option] = optionCounts[option];
        }

        if (changed)
        {
            Utilities.SetStateChanged(controller, "CVoteController", "m_nPotentialVotes");
            Utilities.SetStateChanged(controller, "CVoteController", "m_nVoteOptionCount");
        }
        if (votesCastChanged)
            Utilities.SetStateChanged(controller, "CVoteController", "m_nVotesCast");

        if (!changed && !votesCastChanged && _lastPotentialVotes == potentialVotes)
            return;

        _lastPotentialVotes = potentialVotes;
        Array.Copy(optionCounts, _lastOptionCounts, optionCounts.Length);

        var changedEvent = new EventVoteChanged(false)
        {
            Potentialvotes = potentialVotes,
            VoteOption1 = optionCounts[0],
            VoteOption2 = optionCounts[1],
            VoteOption3 = optionCounts[2],
            VoteOption4 = optionCounts[3],
            VoteOption5 = optionCounts[4],
            Yesvotes = optionCounts[0],
            Novotes = optionCounts[1],
        };
        changedEvent.FireEvent(false);

        Logger.LogDebug("Native vote quorum updated: potential={Potential}, yes={Yes}, no={No}",
            potentialVotes, optionCounts[0], optionCounts[1]);
    }

    // Voter eligibility:
    //   1. The slot is not in the managed-bot snapshot (or the API is
    //      unavailable, in which case the snapshot is empty).
    //   2. If the slot was in the snapshot, its current incarnation
    //      matches the snapshot's incarnation. A slot that was a managed
    //      bot at vote start but now has a different incarnation has been
    //      re-used by something else — count it as eligible.
    //   3. The engine's IsBot flag is false. In player mode, the native
    //      plugin overwrites m_bFakePlayer to 0, but the controller's
    //      IsBot check may still rely on the original bit. The
    //      botidentity:api check above is the authoritative source;
    //      the IsBot fallback catches un-managed native bots.
    private static bool IsEligibleVoter(CCSPlayerController player, IBotIdentityApi? botIdentityApi, Dictionary<int, ulong>? managedBotCache)
    {
        if (!player.IsValid || player.IsHLTV)
            return false;

        if (managedBotCache != null && managedBotCache.TryGetValue(player.Slot, out ulong incarnation))
        {
            if (botIdentityApi?.IsManagedBotIncarnation(player.Slot, incarnation) == true)
                return false;
        }

        if (botIdentityApi?.IsManagedBot(player.Slot) == true)
            return false;

        return !player.IsBot;
    }
}
