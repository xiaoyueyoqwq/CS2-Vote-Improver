# CS2 Vote Improver

CounterStrikeSharp plugin that makes whitelisted `callvote` issues count only
humans. It keeps the native Panorama vote HUD, but the electorate and the
tally are owned by the plugin instead of Valve's `CVoteController`.

## Why the plugin takes over `callvote`

Valve's vote quorum counts every connected player controller, including bots
that `CS2-Bot-Identity` runs in *player* mode. Versions 1.x tried to shrink
that denominator by rewriting identity fields (`m_bFakePlayer`, `FL_FAKECLIENT`,
SteamID mirrors, `CBasePlayerController::m_steamID`, `GetClientXUID`,
`m_nPotentialVotes`, ...). None of it changed Valve's internal count: a live
`changelevel` vote with 1 human still logged `potential=10` and timed out.
See `docs/HANDOVER-FABLE-5.1.md` §7 for the full list of falsified routes.

Version 2.0 stops fighting the native issue. For a whitelisted issue the plugin:

1. intercepts `callvote <Issue> [details]` in a `Pre` command listener and
   returns `Handled`, so no native issue is created;
2. builds the electorate from connected controllers that are not HLTV, not
   engine bots, and not `botidentity:api.IsManagedBot(slot)` (team issues
   filter by the caller's team);
3. shows the native HUD to those players with the `VoteStart` user message,
   and listens to the client command `vote option1` / `vote option2` (F1/F2)
   in a `Pre` command listener. `vote_cast` is kept as a fallback if the
   engine still emits it (`m_iActiveIssueIndex` is set non-negative for HUD
   compatibility, same as CS2Fixes' PanoramaVote);
4. tallies only ballots from the electorate, publishes counts through
   `CVoteController.m_nVoteOptionCount` / `vote_changed`, and decides with
   `ceil(potential * sv_vote_quorum_ratio)` yes votes;
5. sends `VotePass` and runs the issue's command (`game_alias <current>`
   then `changelevel <map>`, `kickid <userid>`, `mp_restartgame 1`,
   `timeout_<team>_start`, ...), or sends `VoteFailed` and starts the
   issue cooldown. `ChangeLevel` reasserts the live `game_type`/`game_mode`
   as a Valve `game_alias` first (same table as CS2-Switch-Gamemode) so a
   bare `changelevel` does not leave the client/loading screen on the
   War Games group.

Anything not whitelisted (Surrender, unknown issues, votes called by bots,
kick votes against bots, maps that fail `IsMapValid`, disabled
`sv_vote_issue_*_allowed` ConVars, an already running native vote) is passed
through untouched to the native handler.

## Requirements

- CounterStrikeSharp API 1.0.371 or a compatible release targeting `net10.0`
- `CS2-Bot-Identity` (native plugin + `BotIdentityImpl`) installed and loaded
- `BotIdentityApi` shared assembly provided by `CS2-Bot-Identity`

The plugin reads managed-bot metadata from the `botidentity:api` capability.
Without it, only the engine's `IsBot` flag is available and player-mode bots
will be counted as humans (set `AllowWithoutBotIdentityApi=false` to fall back
to native voting instead).

Build with:

```bash
dotnet build -c Release
```

Copy `bin/Release/net10.0/BotVoteFix.dll` to:
`game/csgo/addons/counterstrikesharp/plugins/BotVoteFix/`.

The DLL must be directly inside that directory, not in a nested
`BotVoteFix/BotVoteFix/` directory. After a hot reload, verify the active copy
with `css_plugins list` and look for the startup line containing
`Bot Vote Fix v2.0.2`.

The generated configuration is in:
`addons/counterstrikesharp/configs/plugins/BotVoteFix/BotVoteFix.json`.

## Configuration

- `Enabled`: enable the takeover. When false every `callvote` is native.
- `VoteDurationSeconds`: overrides `sv_vote_timer_duration` when > 0.
- `QuorumRatio`: overrides `sv_vote_quorum_ratio` when > 0.
- `FailureCooldownSeconds`: overrides `sv_vote_failure_timer` when >= 0.
- `CallerCooldownSeconds`: overrides `sv_vote_creation_timer` when >= 0.
- `ExecuteDelaySeconds`: delay between `VotePass` and running the command.
- `AllowWithoutBotIdentityApi`: see above.
- `Issues`: per-issue settings keyed by the `callvote` issue name
  (`ChangeLevel`, `Kick`, `RestartGame`, `StartTimeOut`, `NextLevel`,
  `ScrambleTeams`, `SwapTeams`, `Surrender`):
  - `Enabled`: take this issue over (defaults: ChangeLevel, Kick,
    RestartGame, StartTimeOut on; the rest off).
  - `Scope`: `Global` or `Team`.
  - `DisplayString` / `PassedString`: Panorama localisation tokens.
  - `Command`: server command run on pass. Placeholders: `{map}`,
    `{details}`, `{userid}`, `{team}`, `{team_name}`. Empty = never take over.
  - `AllowConVar`: boolean ConVar that must be enabled.
  - `FailedRecentlyReason`: `CallVoteFailed.reason` used while on cooldown.

Issues missing from an existing config file are added disabled.

## Behaviour details

- Rejections (`CallVoteFailed`) use the CS:GO `vote_create_failed_t` codes:
  spectator caller, caller rate limit, issue cooldown, unknown kick target.
- A voter who disconnects mid-vote shrinks the electorate; a kick target who
  disconnects cancels the vote without starting the cooldown.
- The vote ends early once the outcome cannot change (enough yes votes, or
  not enough voters left to reach quorum).
- `callvote Kick` accepts both `Kick <userid> [reason]` and the quoted
  `Kick "<userid> <reason>"` form; the reason picks the matching
  `#SFUI_vote_kick_player_*` token.
- Managed bots in *bot* mode are also excluded (they are `IsBot`); the
  legacy BotHider auto-yes only fires on `vote_options`, which the takeover
  never emits, and its synthetic `vote_cast` events are filtered by slot.

## Scope and limitations

- Only whitelisted issues get a human-only quorum; native votes started by
  other paths (e.g. Surrender, other plugins' issues) keep Valve's count.
- The `callvote` argument format was taken from the CS:GO SDK
  (`callvote ChangeLevel <map>`, `callvote Kick "<userid> <reason>"`,
  `callvote StartTimeOut`). Verify on the live CS2 build; unknown shapes fall
  back to the native handler or a `CallVoteFailed` reply, never a wrong
  command.
- Writing `m_iActiveIssueIndex` while no native issue exists relies on the
  engine not starting `m_acceptingVotesTimer`; this is build-sensitive
  (see `AGENTS.md`) and must be re-checked after CS2 updates together with
  any other plugin that touches `CVoteController`.
- 2.0.1 was checked live: console `vote option1` passed a `ChangeLevel`
  vote (`potential=1`) and ran `changelevel`. F1 still depends on the
  client bind. 2.0.2 adds `game_alias` before `changelevel`; confirm the
  log shows `executing 'game_alias competitive' then 'changelevel ...'`
  when the server is in competitive, and that the loading screen stays
  on the current mode.
