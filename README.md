# CS2 Vote Improver

CounterStrikeSharp plugin that removes bots from the eligible-voter count of
native CS2 votes. It keeps the native Panorama vote UI and listens to the
native vote lifecycle.

## Requirements

- CounterStrikeSharp API 1.0.371 or a compatible release targeting `net10.0`
- `CS2-Bot-Identity` (native plugin) installed and loaded
- `BotIdentityApi` shared assembly provided by `CS2-Bot-Identity`

The plugin reads managed-bot metadata from the `botidentity:api`
capability published by `CS2-Bot-Identity` (via the `/dev/shm/CS2BotHider_Slots`
shared region the native plugin writes).

Build with:

```bash
dotnet build -c Release
```

Copy `bin/Release/net10.0/BotVoteFix.dll` to:
`game/csgo/addons/counterstrikesharp/plugins/BotVoteFix/`.

The DLL must be directly inside that directory, not in a nested
`BotVoteFix/BotVoteFix/` directory. After a hot reload, verify the active copy
with `css_plugins list` and look for the startup line containing
`Bot Vote Fix v1.1.2`.

The generated configuration is in:
`addons/counterstrikesharp/configs/plugins/BotVoteFix/BotVoteFix.json`.

## Configuration

- `Enabled`: enable the plugin.
- `RefreshIntervalSeconds`: how often the active vote controller is reconciled
  (0.05-1.0 seconds).
- `MinimumPotentialVotes`: lower bound for `m_nPotentialVotes`; keep this at 1
  to prevent a zero-player vote from being treated as an immediate pass.

The voter pool excludes invalid clients, HLTV, native bots, and any slot where
`botidentity:api` reports `IsManagedBot(slot) == true`.

Vote tracking is armed from `vote_options` as well as `vote_started`; the former
is emitted earlier in the native vote creation path. The first reconciliation is
performed synchronously and subsequent passes run on the configured timer. The
plugin logs `[VoteFix] Native vote tracking armed by ...` when this path is active.

On `vote_options`, and again on every refresh tick, the plugin writes
`CBasePlayerController::m_steamID = 0` through CSS Schema for each managed
bot still matching the vote-start incarnation. That write does **not** call
`SetStateChanged`, so the scoreboard should not flash an empty SteamID.
BotHiderImpl's 0.25s/2s reconcile can put the synthetic SteamID back; the
refresh loop covers it. The snapshotted SteamID is restored when the vote
ends (including timeout / vanished controller). `CVoteController` field
rewrites remain HUD/log probes only — they do not change Valve's internal
quorum. Pair this with BotIdentity 0.1.3; a global `changelevel` vote is
the test that matters, not a team timeout with one human and one teammate
bot. Live 0.1.3/1.1.2 still timed out a changelevel with Valve
`potential=10` while schema SteamID and engine XUID were 0. Stop
zeroing identity fields. Handover brief: `docs/HANDOVER-FABLE-5.1.md`.

### Bot-Identity compatibility

This plugin uses the `botidentity:api` capability exclusively and does not
depend on the legacy `bothider:api`. The voter pool is recomputed against
the snapshot the native plugin published at vote start, then re-validated
each tick against the current `Incarnation` value — a slot that was a
managed bot at vote start but has since been re-used is correctly reclassified
as eligible. If the native plugin is not loaded, the plugin falls back to
the engine's `IsBot` flag and logs a warning at startup.

## Scope and limitations

This changes the native controller's potential-vote denominator and removes
non-human cast entries while a vote is active. It does not make bots cast a
choice and it does not replace the server's vote policy (`sv_vote_quorum_ratio`,
issue permissions, or vote-specific rules).

If no `[VoteFix] Bot Vote Fix loaded` or `Native vote tracking armed` message is
present after loading the DLL, the server is running a different plugin copy or
the plugin did not load; native vote behavior cannot be diagnosed from the HUD
alone. A live test must also confirm the first `Eligible voters` count in the
server log, using the exact CounterStrikeSharp and CS2 builds in production.

The plugin must be tested against the exact CS2 build and other vote plugins
running on the server. Plugins that overwrite `CVoteController` every frame,
or that start their own custom menu votes, may need an integration setting or
should be disabled for the affected vote.

No dedicated public plugin was found that specifically fixes native vote
quorum for regular bots or BotHider. Existing projects such as
[NativeVoteAPI-CS2](https://github.com/fltuna/NativeVoteAPI-CS2) provide an API
for creating replacement native votes, while map-vote plugins generally manage
their own vote pool.
