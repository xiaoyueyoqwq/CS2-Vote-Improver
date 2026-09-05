using System.Text.Json.Serialization;
using BotIdentityApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using CssTimer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace BotVoteFix;

public sealed class BotVoteFixConfig : BasePluginConfig
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Overrides sv_vote_timer_duration when &gt; 0.</summary>
    [JsonPropertyName("VoteDurationSeconds")]
    public float VoteDurationSeconds { get; set; } = 0f;

    /// <summary>Overrides sv_vote_quorum_ratio when &gt; 0.</summary>
    [JsonPropertyName("QuorumRatio")]
    public float QuorumRatio { get; set; } = 0f;

    /// <summary>Overrides sv_vote_failure_timer when &gt;= 0.</summary>
    [JsonPropertyName("FailureCooldownSeconds")]
    public float FailureCooldownSeconds { get; set; } = -1f;

    /// <summary>Overrides sv_vote_creation_timer when &gt;= 0.</summary>
    [JsonPropertyName("CallerCooldownSeconds")]
    public float CallerCooldownSeconds { get; set; } = -1f;

    /// <summary>Delay between VotePass and executing the command.</summary>
    [JsonPropertyName("ExecuteDelaySeconds")]
    public float ExecuteDelaySeconds { get; set; } = 3f;

    /// <summary>
    /// When the botidentity:api capability is missing, fall back to the
    /// engine's IsBot flag only (managed bots in player mode will then count).
    /// Set to false to refuse takeover without the API.
    /// </summary>
    [JsonPropertyName("AllowWithoutBotIdentityApi")]
    public bool AllowWithoutBotIdentityApi { get; set; } = true;

    [JsonPropertyName("Issues")]
    public Dictionary<string, IssueConfig> Issues { get; set; } = IssueConfig.Defaults();
}

/// <summary>
/// Takes over whitelisted <c>callvote</c> issues so that only humans form
/// the electorate. Valve's own vote quorum counts managed bots and none of
/// the identity-field rewrites tried in 1.x changed that (see
/// docs/HANDOVER-FABLE-5.1.md §7), so instead of a native issue we run a
/// plugin-owned yes/no vote on the native Panorama HUD and execute the
/// issue's command ourselves when it passes.
/// </summary>
[MinimumApiVersion(334)]
public sealed class BotVoteFix : BasePlugin, IPluginConfig<BotVoteFixConfig>
{
    private const float DefaultVoteDuration = 15f;
    private const float DefaultQuorumRatio = 0.501f;
    private const float DefaultFailureCooldown = 300f;
    private const float DefaultCallerCooldown = 150f;

    private static readonly PluginCapability<IBotIdentityApi> CapabilityToken = new("botidentity:api");

    private IBotIdentityApi? _botIdentityApi;
    private HumanVote? _activeVote;
    private CssTimer? _timeoutTimer;
    private CssTimer? _executeTimer;
    private readonly Dictionary<string, float> _issueCooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, float> _callerCooldownUntil = new();

    public override string ModuleName => "Bot Vote Fix";
    public override string ModuleVersion => "2.0.1";
    public override string ModuleAuthor => "CS2-Vote-Improver";
    public override string ModuleDescription =>
        "Runs whitelisted callvote issues with a humans-only electorate (managed bots excluded via botidentity:api).";

    public BotVoteFixConfig Config { get; set; } = new();

    public override void Load(bool hotReload)
    {
        Logger.LogInformation("[VoteFix] Loading Bot Vote Fix v{Version}. Enabled={Enabled}",
            ModuleVersion, Config.Enabled);

        AddCommandListener("callvote", OnCallVote, HookMode.Pre);
        AddCommandListener("vote", OnVoteCommand, HookMode.Pre);
        RegisterEventHandler<EventVoteCast>(OnVoteCast);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        if (hotReload)
        {
            _botIdentityApi = ResolveBotIdentityApi();
        }
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _botIdentityApi = ResolveBotIdentityApi();
        Logger.LogInformation("[VoteFix] botidentity:api {State}; managed bots will {Treatment}.",
            _botIdentityApi == null ? "not available" : "available",
            _botIdentityApi == null ? "only be excluded when the engine flags them as bots" : "be excluded explicitly");
    }

    public override void Unload(bool hotReload)
    {
        CancelActiveVote("plugin unload");
        RemoveCommandListener("callvote", OnCallVote, HookMode.Pre);
        RemoveCommandListener("vote", OnVoteCommand, HookMode.Pre);
        DeregisterEventHandler<EventVoteCast>(OnVoteCast);
        DeregisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
    }

    public void OnConfigParsed(BotVoteFixConfig config)
    {
        config.ExecuteDelaySeconds = Math.Clamp(config.ExecuteDelaySeconds, 0f, 30f);
        config.Issues ??= IssueConfig.Defaults();
        foreach (var (name, defaults) in IssueConfig.Defaults())
        {
            if (!config.Issues.ContainsKey(name))
            {
                defaults.Enabled = false;
                config.Issues[name] = defaults;
            }
        }
        Config = config;
    }

    private IBotIdentityApi? ResolveBotIdentityApi()
    {
        try { return CapabilityToken.Get(); }
        catch { return null; }
    }

    // ---------------------------------------------------------------------
    // callvote interception
    // ---------------------------------------------------------------------

    private HookResult OnCallVote(CCSPlayerController? caller, CommandInfo command)
    {
        if (!Config.Enabled) return HookResult.Continue;
        if (caller == null || !caller.IsValid) return HookResult.Continue;
        if (command.ArgCount < 2) return HookResult.Continue;

        try
        {
            return HandleCallVote(caller, command);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "[VoteFix] callvote takeover failed; passing '{Args}' to the native handler", command.ArgString);
            return HookResult.Continue;
        }
    }

    private HookResult HandleCallVote(CCSPlayerController caller, CommandInfo command)
    {
        string issueType = command.GetArg(1).Trim();
        if (!Config.Issues.TryGetValue(issueType, out var issue) || !issue.Enabled || string.IsNullOrWhiteSpace(issue.Command))
        {
            Logger.LogDebug("[VoteFix] callvote {Issue}: not whitelisted, native handles it", issueType);
            return HookResult.Continue;
        }

        if (!string.IsNullOrEmpty(issue.AllowConVar))
        {
            var allow = ConVar.Find(issue.AllowConVar);
            if (allow != null && !allow.GetPrimitiveValue<bool>())
            {
                Logger.LogDebug("[VoteFix] callvote {Issue}: {ConVar} is 0, native handles it", issueType, issue.AllowConVar);
                return HookResult.Continue;
            }
        }

        // Managed bots never call votes on their own; a native bot or HLTV should stay on the native path.
        if (caller.IsHLTV || caller.IsBot || _botIdentityApi?.IsManagedBot(caller.Slot) == true)
            return HookResult.Continue;

        if (_botIdentityApi == null && !Config.AllowWithoutBotIdentityApi)
        {
            Logger.LogWarning("[VoteFix] callvote {Issue}: botidentity:api unavailable and AllowWithoutBotIdentityApi=false, native handles it", issueType);
            return HookResult.Continue;
        }

        var controller = FindVoteController();
        if (controller != null && controller.IsValid && controller.ActiveIssueIndex >= 0 && _activeVote == null)
        {
            Logger.LogDebug("[VoteFix] callvote {Issue}: a native vote is active, native handles it", issueType);
            return HookResult.Continue;
        }

        string details = JoinArgs(command, 2);
        var callerTeam = caller.Team;

        if (callerTeam != CsTeam.Terrorist && callerTeam != CsTeam.CounterTerrorist)
        {
            var allowSpectators = ConVar.Find("sv_vote_allow_spectators");
            if (allowSpectators == null || !allowSpectators.GetPrimitiveValue<bool>())
                return RejectCall(caller, VoteFailReason.Spectator);
        }

        if (_activeVote != null)
            return RejectCall(caller, VoteFailReason.Generic);

        float now = Server.CurrentTime;
        if (caller.SteamID != 0 && _callerCooldownUntil.TryGetValue(caller.SteamID, out float callerUntil) && callerUntil > now)
            return RejectCall(caller, VoteFailReason.RateExceeded, (int)Math.Ceiling(callerUntil - now));

        int targetUserId = -1;
        int targetSlot = -1;
        string detailsForUi = details;

        if (string.Equals(issueType, "Kick", StringComparison.OrdinalIgnoreCase))
        {
            if (!CallVoteRequest.TryParseKickTarget(details, out targetUserId))
                return RejectCall(caller, VoteFailReason.PlayerNotFound);

            var target = Utilities.GetPlayerFromUserid(targetUserId);
            if (target == null || !target.IsValid || target.Connected != PlayerConnectedState.Connected)
                return RejectCall(caller, VoteFailReason.PlayerNotFound);

            // Kicking bots (managed or native) stays on the native path.
            if (target.IsBot || target.IsHLTV || _botIdentityApi?.IsManagedBot(target.Slot) == true)
                return HookResult.Continue;

            targetSlot = target.Slot;
            detailsForUi = target.PlayerName;
        }
        else if (string.Equals(issueType, "ChangeLevel", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(issueType, "NextLevel", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(details) || !IsSafeMapName(details))
                return RejectCall(caller, VoteFailReason.MapNotFound);
            if (!Server.IsMapValid(details))
            {
                // Workshop / unknown names: let the native issue decide instead of guessing.
                Logger.LogInformation("[VoteFix] callvote {Issue} '{Map}': IsMapValid=false, native handles it", issueType, details);
                return HookResult.Continue;
            }
        }

        var request = new CallVoteRequest
        {
            IssueType = issueType,
            Details = details,
            DetailsForUi = detailsForUi,
            Issue = issue,
            CallerSlot = caller.Slot,
            CallerTeam = callerTeam,
            TargetUserId = targetUserId,
            TargetSlot = targetSlot,
        };

        if (_issueCooldownUntil.TryGetValue(request.CooldownKey, out float issueUntil) && issueUntil > now)
            return RejectCall(caller, issue.FailedRecentlyReason, (int)Math.Ceiling(issueUntil - now));

        var voters = CollectHumanVoters(request);
        if (voters.Count == 0)
        {
            Logger.LogWarning("[VoteFix] callvote {Issue}: no human voters found (caller slot {Slot}); native handles it",
                issueType, caller.Slot);
            return HookResult.Continue;
        }

        StartVote(request, voters, controller);
        if (caller.SteamID != 0)
            _callerCooldownUntil[caller.SteamID] = now + CallerCooldown();
        return HookResult.Handled;
    }

    private HookResult RejectCall(CCSPlayerController caller, int reason, int seconds = 0)
    {
        var failed = UserMessage.FromId(HumanVote.UmCallVoteFailed);
        failed.SetInt("reason", reason);
        failed.SetInt("time", seconds);
        failed.Send(new RecipientFilter(caller));
        Logger.LogInformation("[VoteFix] callvote rejected for slot {Slot}: reason={Reason} time={Time}",
            caller.Slot, reason, seconds);
        return HookResult.Handled;
    }

    private static string JoinArgs(CommandInfo command, int startIndex)
    {
        var parts = new List<string>();
        for (int i = startIndex; i < command.ArgCount; i++)
        {
            string arg = command.GetArg(i).Trim();
            if (arg.Length > 0) parts.Add(arg);
        }
        return string.Join(' ', parts);
    }

    private static bool IsSafeMapName(string map)
    {
        if (map.Length > 64) return false;
        foreach (char c in map)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '/'))
                return false;
        }
        return !map.Contains("..", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // electorate
    // ---------------------------------------------------------------------

    private List<int> CollectHumanVoters(CallVoteRequest request)
    {
        var voters = new List<int>();
        var api = _botIdentityApi;
        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsHumanVoter(player, api)) continue;
            if (request.IsTeamVote && player.Team != request.CallerTeam) continue;
            voters.Add(player.Slot);
        }
        return voters;
    }

    // Managed bots in player mode have m_bFakePlayer cleared by the native
    // BotIdentity plugin, so IsBot alone cannot see them; botidentity:api is
    // the authoritative source and IsBot only catches un-managed native bots.
    private static bool IsHumanVoter(CCSPlayerController player, IBotIdentityApi? api)
    {
        if (!player.IsValid || player.IsHLTV) return false;
        if (player.Connected != PlayerConnectedState.Connected) return false;
        if (player.IsBot) return false;
        if (api?.IsManagedBot(player.Slot) == true) return false;
        return true;
    }

    // ---------------------------------------------------------------------
    // vote lifecycle
    // ---------------------------------------------------------------------

    private void StartVote(CallVoteRequest request, List<int> voters, CVoteController? controller)
    {
        var vote = new HumanVote(request, voters, QuorumRatio(), message => Logger.LogInformation("[VoteFix] {Message}", message));
        _activeVote = vote;
        vote.Start(controller);

        float duration = VoteDuration();
        _timeoutTimer?.Kill();
        _timeoutTimer = AddTimer(duration, () =>
        {
            if (!ReferenceEquals(_activeVote, vote) || vote.IsFinished) return;
            Conclude(vote, vote.EvaluateAtTimeout());
        });

        var early = vote.EvaluateEarly();
        if (early != null) Conclude(vote, early.Value);
    }

    private HookResult OnVoteCommand(CCSPlayerController? caller, CommandInfo command)
    {
        var vote = _activeVote;
        if (vote == null || vote.IsFinished) return HookResult.Continue;
        if (caller == null || !caller.IsValid) return HookResult.Continue;
        if (!TryParseVoteOption(command, out int option)) return HookResult.Continue;

        try
        {
            ApplyBallot(caller.Slot, option);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "[VoteFix] vote command tally failed for '{Args}'", command.ArgString);
        }

        return HookResult.Handled;
    }

    private HookResult OnVoteCast(EventVoteCast @event, GameEventInfo info)
    {
        var vote = _activeVote;
        if (vote == null || vote.IsFinished) return HookResult.Continue;

        var voter = @event.Userid;
        if (voter == null || !voter.IsValid) return HookResult.Continue;

        ApplyBallot(voter.Slot, @event.VoteOption);
        return HookResult.Continue;
    }

    private void ApplyBallot(int slot, int option)
    {
        var vote = _activeVote;
        if (vote == null || vote.IsFinished) return;

        if (vote.TryCast(slot, option, FindVoteController()))
        {
            var early = vote.EvaluateEarly();
            if (early != null) Conclude(vote, early.Value);
        }
    }

    private static bool TryParseVoteOption(CommandInfo command, out int option)
    {
        option = -1;
        if (command.ArgCount < 2) return false;

        string token = command.GetArg(1).Trim();
        if (token.Equals("option1", StringComparison.OrdinalIgnoreCase))
        {
            option = 0;
            return true;
        }
        if (token.Equals("option2", StringComparison.OrdinalIgnoreCase))
        {
            option = 1;
            return true;
        }
        return false;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var vote = _activeVote;
        var player = @event.Userid;
        if (vote == null || vote.IsFinished || player == null) return HookResult.Continue;

        if (vote.Request.TargetSlot >= 0 && vote.Request.TargetSlot == player.Slot)
        {
            Logger.LogInformation("[VoteFix] kick target left during vote; cancelling");
            Conclude(vote, HumanVoteOutcome.Cancelled);
            return HookResult.Continue;
        }

        if (!vote.IsVoter(player.Slot)) return HookResult.Continue;

        vote.RemoveVoter(player.Slot, FindVoteController());
        var early = vote.EvaluateEarly();
        if (early != null) Conclude(vote, early.Value);
        return HookResult.Continue;
    }

    private void OnMapEnd()
    {
        CancelActiveVote("map end");
        _issueCooldownUntil.Clear();
        _callerCooldownUntil.Clear();
    }

    private void Conclude(HumanVote vote, HumanVoteOutcome outcome)
    {
        if (!ReferenceEquals(_activeVote, vote)) return;

        _timeoutTimer?.Kill();
        _timeoutTimer = null;
        _activeVote = null;

        var controller = FindVoteController();
        vote.Finish(outcome, controller);

        if (outcome != HumanVoteOutcome.Passed)
        {
            if (outcome != HumanVoteOutcome.Cancelled)
                _issueCooldownUntil[vote.Request.CooldownKey] = Server.CurrentTime + FailureCooldown();
            return;
        }

        string commandText = vote.Request.BuildCommand();
        if (string.IsNullOrWhiteSpace(commandText)) return;

        _executeTimer?.Kill();
        _executeTimer = AddTimer(Config.ExecuteDelaySeconds, () =>
        {
            _executeTimer = null;
            Logger.LogInformation("[VoteFix] executing '{Command}' for passed {Issue} vote", commandText, vote.Request.IssueType);
            Server.ExecuteCommand(commandText);
        });
    }

    private void CancelActiveVote(string reason)
    {
        _timeoutTimer?.Kill();
        _timeoutTimer = null;
        _executeTimer?.Kill();
        _executeTimer = null;

        var vote = _activeVote;
        if (vote == null) return;
        _activeVote = null;
        Logger.LogInformation("[VoteFix] cancelling active vote: {Reason}", reason);
        try { vote.Finish(HumanVoteOutcome.Cancelled, FindVoteController()); }
        catch (Exception exception) { Logger.LogWarning(exception, "[VoteFix] cancel failed"); }
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static CVoteController? FindVoteController()
    {
        return Utilities
            .FindAllEntitiesByDesignerName<CVoteController>("vote_controller")
            .FirstOrDefault(controller => controller.IsValid);
    }

    private float VoteDuration()
    {
        if (Config.VoteDurationSeconds > 0f) return Config.VoteDurationSeconds;
        return ReadFloat("sv_vote_timer_duration", DefaultVoteDuration, 1f, 300f);
    }

    private float QuorumRatio()
    {
        if (Config.QuorumRatio > 0f) return Math.Clamp(Config.QuorumRatio, 0.01f, 1f);
        return ReadFloat("sv_vote_quorum_ratio", DefaultQuorumRatio, 0.01f, 1f);
    }

    private float FailureCooldown()
    {
        if (Config.FailureCooldownSeconds >= 0f) return Config.FailureCooldownSeconds;
        return ReadFloat("sv_vote_failure_timer", DefaultFailureCooldown, 0f, 3600f);
    }

    private float CallerCooldown()
    {
        if (Config.CallerCooldownSeconds >= 0f) return Config.CallerCooldownSeconds;
        return ReadFloat("sv_vote_creation_timer", DefaultCallerCooldown, 0f, 3600f);
    }

    private float ReadFloat(string name, float fallback, float min, float max)
    {
        var cvar = ConVar.Find(name);
        if (cvar == null) return fallback;
        try
        {
            float value = cvar.Type switch
            {
                ConVarType.Float32 => cvar.GetPrimitiveValue<float>(),
                ConVarType.Float64 => (float)cvar.GetPrimitiveValue<double>(),
                ConVarType.Int32 => cvar.GetPrimitiveValue<int>(),
                ConVarType.Int16 => cvar.GetPrimitiveValue<short>(),
                ConVarType.Int64 => cvar.GetPrimitiveValue<long>(),
                ConVarType.UInt32 => cvar.GetPrimitiveValue<uint>(),
                _ => fallback,
            };
            return Math.Clamp(value, min, max);
        }
        catch (Exception exception)
        {
            Logger.LogDebug(exception, "[VoteFix] failed to read {ConVar}", name);
            return fallback;
        }
    }
}
