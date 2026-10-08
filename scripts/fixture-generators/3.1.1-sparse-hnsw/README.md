# LeanCorpus 3.1.1 sparse-vector fixture source

Run `dotnet run --project scripts/fixture-generators/3.1.1-sparse-hnsw/FixtureGenerator.csproj -- /path/to/empty-output`.
The project references the published `LeanCorpus` 3.1.1 NuGet package and pins
an installed .NET 10 SDK. `Program.cs` checks the loaded assembly version
before writing and refuses to append to an existing output.

The four outputs cover Float32 (`.vec`) and Int8 (`.vq`) vectors in loose and
compound indexes. Each index has two three-document segments. Local documents
0 and 1 have vectors and document 2 has no vector; both vector-bearing IDs are
persisted in that segment's 3.1.1 HNSW graph. This exercises sparse presence
reconstruction from graph membership rather than values or a current-code
fixture writer.

The published package archive used to produce the checked-in snapshot has
SHA-256 `cf124d5a2b9b3bf623cd69f98ea25c5a2e3f7d67ac9bdb4c017a60a00d00e478`.
The compressed fixture archive has SHA-256
`ebaec3b42819af4c35049e589d75fc682dd4b6169ad85fbf2e7b7197e066a961`; this
hash is verified by `HistoricalIndexFixtures` before extraction.
