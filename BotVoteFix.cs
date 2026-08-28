using System.Text.Json.Serialization;
using BotHiderApi;
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
    private IBotHiderApi? _botHiderApi;
    private bool _voteActive;
    private int _lastPotentialVotes = -1;
    private readonly int[] _lastOptionCounts = new int[5];

    public override string ModuleName => "Bot Vote Fix";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "CS2-Vote-Improver";
    public override string ModuleDescription => "Excludes bots from native vote quorum calculations.";

    public BotVoteFixConfig Config { get; set; } = new();

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventVoteStarted>(OnVoteStarted);
        RegisterEventHandler<EventVoteEnded>(OnVoteFinished);
        RegisterEventHandler<EventVotePassed>(OnVoteFinished);
        RegisterEventHandler<EventVoteFailed>(OnVoteFinished);

        Logger.LogInformation("Bot Vote Fix loaded. Enabled={Enabled}", Config.Enabled);
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _botHiderApi = new PluginCapability<IBotHiderApi>("bothider:api").Get();
        Logger.LogInformation("BotHider API {State}; managed bots will {Treatment}.",
            _botHiderApi == null ? "not available" : "available",
            _botHiderApi == null ? "fall back to IsBot detection" : "be excluded explicitly");
    }

    public override void Unload(bool hotReload)
    {
        _refreshTimer?.Kill();
        _refreshTimer = null;

        DeregisterEventHandler<EventVoteStarted>(OnVoteStarted);
        DeregisterEventHandler<EventVoteEnded>(OnVoteFinished);
        DeregisterEventHandler<EventVotePassed>(OnVoteFinished);
        DeregisterEventHandler<EventVoteFailed>(OnVoteFinished);
    }

    public void OnConfigParsed(BotVoteFixConfig config)
    {
        config.RefreshIntervalSeconds = Math.Clamp(config.RefreshIntervalSeconds, 0.05f, 1.0f);
        // Server.MaxPlayers is a native global and is not initialized while
        // CounterStrikeSharp is loading plugin configuration.
        config.MinimumPotentialVotes = Math.Max(config.MinimumPotentialVotes, 0);
        Config = config;
    }

    private HookResult OnVoteStarted(EventVoteStarted @event, GameEventInfo info)
    {
        if (!Config.Enabled)
            return HookResult.Continue;

        _voteActive = true;
        _lastPotentialVotes = -1;
        Array.Fill(_lastOptionCounts, -1);

        _refreshTimer?.Kill();
        _refreshTimer = AddTimer(Config.RefreshIntervalSeconds, RefreshVote, TimerFlags.REPEAT);

        // The vote controller is sometimes populated one frame after vote_started.
        AddTimer(0.0f, RefreshVote);
        return HookResult.Continue;
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
    }

    private void RefreshVote()
    {
        if (!Config.Enabled || !_voteActive)
            return;

        var controller = Utilities
            .FindAllEntitiesByDesignerName<CVoteController>("vote_controller")
            .LastOrDefault(entity => entity.IsValid);

        if (controller == null)
            return;

        var maxSlots = controller.VotesCast.Length;
        var eligibleSlots = Utilities.GetPlayers()
            .Where(player => IsEligibleVoter(player, _botHiderApi))
            .Select(player => player.Slot)
            .Where(slot => slot >= 0 && slot < maxSlots)
            .ToHashSet();

        var potentialVotes = Math.Max(eligibleSlots.Count, Config.MinimumPotentialVotes);
        var optionCounts = new int[5];

        // Rebuild counts from eligible slots. This also removes votes left by bots,
        // BotHider clients, and players who disconnected during the vote.
        for (var slot = 0; slot < maxSlots; slot++)
        {
            var cast = controller.VotesCast[slot];
            if (!eligibleSlots.Contains(slot))
            {
                if (cast != UncastVote)
                    controller.VotesCast[slot] = UncastVote;
                continue;
            }

            if (cast is >= 0 and < 5)
                optionCounts[cast]++;
            else if (cast != UncastVote)
                controller.VotesCast[slot] = UncastVote;
        }

        var changed = controller.PotentialVotes != potentialVotes;
        controller.PotentialVotes = potentialVotes;

        for (var option = 0; option < optionCounts.Length; option++)
        {
            changed |= controller.VoteOptionCount[option] != optionCounts[option];
            controller.VoteOptionCount[option] = optionCounts[option];
        }

        if (!changed && _lastPotentialVotes == potentialVotes)
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
            Novotes = optionCounts[1]
        };
        changedEvent.FireEvent(false);

        Logger.LogDebug("Native vote quorum updated: potential={Potential}, yes={Yes}, no={No}",
            potentialVotes, optionCounts[0], optionCounts[1]);
    }

    private static bool IsEligibleVoter(CCSPlayerController player, IBotHiderApi? botHiderApi)
    {
        if (!player.IsValid || player.IsHLTV)
            return false;

        // BotHider's player mode can make a fake client look human to engine
        // consumers. Its capability is the authoritative managed-slot check.
        if (botHiderApi?.IsManagedBot(player.Slot) == true)
            return false;

        return !player.IsBot;
    }
}
