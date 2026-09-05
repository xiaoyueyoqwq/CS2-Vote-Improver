using System.Text.Json.Serialization;
using BotIdentityApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Memory;
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
    private int _lastEligibleCount = -1;
    private int _lastEligibleTotal = -1;

    /// <summary>
    /// Snapshot of managed bot slots captured at vote start. Used to
    /// validate per-slot incarnation so a slot reuse during the vote
    /// (player disconnect, manual bot_kick) doesn't accidentally
    /// include a fresh human voter.
    /// </summary>
    private Dictionary<int, ulong>? _managedBotIncarnations;
    private Dictionary<int, ulong>? _schemaSteamIdSnapshots;
    private bool _schemaOffsetLogged;

    public override string ModuleName => "Bot Vote Fix";
    public override string ModuleVersion => "1.1.2";
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
        try
        {
            RefreshVote();
        }
        catch (Exception exception)
        {
            // A broken vote event payload (e.g. no eligible voters) must not
            // kill the armed vote-tracking state.
            Logger.LogError(exception, "[VoteFix] RefreshVote failed after vote_options");
        }
        return HookResult.Continue;
    }

    private HookResult OnVoteStarted(EventVoteStarted @event, GameEventInfo info)
    {
        ArmVote("vote_started");
        return HookResult.Continue;
    }

    private HookResult OnVoteCast(EventVoteCast @event, GameEventInfo info)
    {
        Logger.LogInformation("[VoteFix] vote_cast: voter={Voter} slot={Slot} option={Option} team={Team}",
            @event.Userid?.PlayerName ?? "?",
            @event.Userid?.Slot ?? -1,
            @event.VoteOption,
            @event.Team);
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

        // Zero schema SteamID on this call stack, before vote_options returns
        // and Valve's first Think writes potential. Do not wait for NextFrame.
        CaptureAndZeroManagedSteamIds();
        AddTimer(0.0f, RefreshVote);
    }

    private HookResult OnVoteFinished(GameEvent @event, GameEventInfo info)
    {
        Logger.LogInformation("[VoteFix] vote finished: {Event}", @event.EventName);
        StopRefreshing();
        return HookResult.Continue;
    }

    private void StopRefreshing()
    {
        RestoreManagedSteamIds();
        _voteActive = false;
        _refreshTimer?.Kill();
        _refreshTimer = null;
        _lastPotentialVotes = -1;
        _lastEligibleCount = -1;
        _lastEligibleTotal = -1;
        Array.Fill(_lastOptionCounts, -1);
        _managedBotIncarnations = null;
        _voteArmLogged = false;
    }

    private void CaptureAndZeroManagedSteamIds()
    {
        if (_managedBotIncarnations == null || _managedBotIncarnations.Count == 0)
            return;

        _schemaSteamIdSnapshots ??= new Dictionary<int, ulong>();
        LogSchemaOffsetOnce();

        foreach (var entry in _managedBotIncarnations)
        {
            var slot = entry.Key;
            var incarnation = entry.Value;
            if (_botIdentityApi?.IsManagedBotIncarnation(slot, incarnation) != true)
                continue;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid)
                continue;

            if (!_schemaSteamIdSnapshots.ContainsKey(slot))
            {
                var schemaSid = player.SteamID;
                var restoreSid = schemaSid != 0
                    ? schemaSid
                    : _botIdentityApi?.GetBotSteamId(slot) ?? 0UL;
                _schemaSteamIdSnapshots[slot] = restoreSid;
                Logger.LogInformation(
                    "[VoteFix] schema steamid snapshot slot={Slot} handle=0x{Handle:X} steamid={Sid} restore={Restore} netid={NetId}",
                    slot, (long)player.Handle, schemaSid, restoreSid, player.NetworkIDString ?? "");
            }

            try
            {
                Schema.SetSchemaValue(player.Handle, "CBasePlayerController", "m_steamID", 0UL);
            }
            catch (Exception exception)
            {
                Logger.LogError(exception, "[VoteFix] schema steamid zero failed slot={Slot}", slot);
            }
        }
    }

    private void RestoreManagedSteamIds()
    {
        if (_schemaSteamIdSnapshots == null)
            return;

        foreach (var entry in _schemaSteamIdSnapshots)
        {
            var slot = entry.Key;
            var steamId = entry.Value;
            if (_managedBotIncarnations != null &&
                _managedBotIncarnations.TryGetValue(slot, out var incarnation) &&
                _botIdentityApi?.IsManagedBotIncarnation(slot, incarnation) != true)
            {
                continue;
            }

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid)
                continue;

            try
            {
                Schema.SetSchemaValue(player.Handle, "CBasePlayerController", "m_steamID", steamId);
            }
            catch (Exception exception)
            {
                Logger.LogError(exception, "[VoteFix] schema steamid restore failed slot={Slot}", slot);
            }
        }

        _schemaSteamIdSnapshots = null;
    }

    private void LogSchemaOffsetOnce()
    {
        if (_schemaOffsetLogged)
            return;
        _schemaOffsetLogged = true;
        try
        {
            var offset = Schema.GetSchemaOffset("CBasePlayerController", "m_steamID");
            Logger.LogInformation("[VoteFix] schema m_steamID offset={Offset}", offset);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "[VoteFix] Schema.GetSchemaOffset m_steamID failed");
        }
    }

    private void RefreshVote()
    {
        if (!Config.Enabled || !_voteActive)
            return;

        CaptureAndZeroManagedSteamIds();

        var controller = Utilities
            .FindAllEntitiesByDesignerName<CVoteController>("vote_controller")
            .LastOrDefault(entity => entity.IsValid && entity.ActiveIssueIndex >= 0);

        if (controller == null)
        {
            // Vote controller vanished (vote ended or started but never completed).
            // Stop refreshing to avoid spamming the log.
            StopRefreshing();
            Logger.LogInformation("[VoteFix] RefreshVote: controller vanished, stopping refresh loop");
            return;
        }

        var maxSlots = controller.VotesCast.Length;
        var allPlayers = Utilities.GetPlayers().Where(p => p.IsValid).ToList();

        var eligibleSlots = allPlayers
            .Where(player => IsEligibleVoter(player, _botIdentityApi, _managedBotIncarnations))
            .Select(player => player.Slot)
            .Where(slot => slot >= 0 && slot < maxSlots)
            .ToHashSet();

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
        var oldPotential = controller.PotentialVotes;
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
            Logger.LogInformation("[VoteFix] SetStateChanged: potential {Old} -> {New}, eligible {Eligible}/{Total} (threshold @ {Ratio:P0})",
                oldPotential, potentialVotes, eligibleSlots.Count, allPlayers.Count, 0.501);
        }

        if (votesCastChanged)
            Utilities.SetStateChanged(controller, "CVoteController", "m_nVotesCast");

        if (!changed && !votesCastChanged && _lastPotentialVotes == potentialVotes)
            return;

        var eligibleChanged = eligibleSlots.Count != _lastEligibleCount || allPlayers.Count != _lastEligibleTotal;
        _lastEligibleCount = eligibleSlots.Count;
        _lastEligibleTotal = allPlayers.Count;
        if (eligibleChanged)
        {
            Logger.LogInformation("[VoteFix] Eligible voters: {Count}/{Total}",
                eligibleSlots.Count, allPlayers.Count);
        }

        _lastPotentialVotes = potentialVotes;
        Array.Copy(optionCounts, _lastOptionCounts, optionCounts.Length);

        try
        {
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
        }
        catch (Exception exception)
        {
            // FireEvent can throw NativeException("Invalid game event") when
            // the engine has no live vote event to clone (observed when the
            // vote had zero eligible voters). The controller writes above are
            // already applied; skip the client-side broadcast only.
            Logger.LogDebug(exception, "[VoteFix] vote_changed broadcast skipped");
        }

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
