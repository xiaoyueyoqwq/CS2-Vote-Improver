# Project Notes

- Pin `CounterStrikeSharp.API` to a package that targets the server's .NET runtime; newer NuGet releases may change the target framework without preserving `net8.0` compatibility.
- Native vote behavior is build-sensitive. Validate `CVoteController` field writes on the exact CS2 update and with any plugin that also manages votes.
