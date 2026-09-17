# Project Notes

- Pin `CounterStrikeSharp.API` to a package that targets the server's .NET runtime; newer NuGet releases may change the target framework without preserving `net8.0` compatibility.
- Native vote behavior is build-sensitive. Validate `CVoteController` field writes on the exact CS2 update and with any plugin that also manages votes.
- `voteimprover:api` (`VoteImproverApi.dll`) is the only supported way for other plugins to start a humans-only Panorama vote. Do not add a second `vote` Pre listener in consumers. Deploy `VoteImproverApi.dll` next to `VoteImprover.dll` only. Historical docs still say BotVoteFix; that was the 2.0.x plugin folder and assembly name.
