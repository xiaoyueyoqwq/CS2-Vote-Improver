# CS2 Vote Improver

CounterStrikeSharp plugin that removes bots from the eligible-voter count of
native CS2 votes. It keeps the native Panorama vote UI and listens to the
native vote lifecycle.

## Requirements

- CounterStrikeSharp API 1.0.371 or a compatible release targeting `net10.0`
- BotHider's `BotHiderApi` assembly when BotHider is installed
- CS2 server with `game/csgo/addons/counterstrikesharp/`

Build with:

```bash
dotnet build -c Release
```

Copy `bin/Release/net10.0/BotVoteFix.dll` to:
`game/csgo/addons/counterstrikesharp/plugins/BotVoteFix/`.

The generated configuration is in:
`addons/counterstrikesharp/configs/plugins/BotVoteFix/BotVoteFix.json`.

## Configuration

- `Enabled`: enable the plugin.
- `RefreshIntervalSeconds`: how often the active vote controller is reconciled
  (0.05-1.0 seconds).
- `MinimumPotentialVotes`: lower bound for `m_nPotentialVotes`; keep this at 1
  to prevent a zero-player vote from being treated as an immediate pass.

The voter pool excludes invalid clients, HLTV, native bots, and any slot where
BotHider's `bothider:api` reports `IsManagedBot(slot) == true`.

### BotHider compatibility

Use BotHider's native-bot identity mode. On the current BotHider branch this is
`"identity_mode": "bot"`; BotHider PR #28 adds `"native_bot"` as the canonical
spelling while retaining `"bot"` as an alias. In `player` mode BotHider removes
Valve's native fake-client markers, so the
engine and other plugins can count managed bots as real players. That mode can
cause native votes to time out and can conflict with `bot_quota` or cross-map
population changes. Vote Improver no longer installs a native identity hook;
it relies on Valve's native bot classification and the BotHider API for
bookkeeping.

BotHider 兼容性：请使用原生 Bot 身份模式。当前分支使用
`"identity_mode": "bot"`，PR #28 合并后可使用 `"native_bot"`；`player` 模式会
移除 Valve 的原生 Bot 标志，可能造成原生投票超时，并与 `bot_quota` 或跨地图
人口管理冲突。

## Scope and limitations

This changes the native controller's potential-vote denominator and removes
non-human cast entries while a vote is active. It does not make bots cast a
choice and it does not replace the server's vote policy (`sv_vote_quorum_ratio`,
issue permissions, or vote-specific rules).

The plugin must be tested against the exact CS2 build and other vote plugins
running on the server. Plugins that overwrite `CVoteController` every frame,
or that start their own custom menu votes, may need an integration setting or
should be disabled for the affected vote.

No dedicated public plugin was found that specifically fixes native vote
quorum for regular bots or BotHider. Existing projects such as
[NativeVoteAPI-CS2](https://github.com/fltuna/NativeVoteAPI-CS2) provide an API
for creating replacement native votes, while map-vote plugins generally manage
their own vote pool.
