## What this changes

<!-- One or two sentences. Why, not just what. -->

## How it was verified

<!-- Commands you ran, cases you exercised. -->

## Checklist

- [ ] `dotnet test tests/DotNetSigningServer.Tests/DotNetSigningServer.Tests.csproj` passes
- [ ] `dotnet format dotnet-signing-server.sln --verify-no-changes` is clean
- [ ] Tests cover the changed behaviour
- [ ] User-facing strings go through `Resources/SharedStrings.*.resx`, with **EN and CS both updated**
- [ ] New configuration is documented in `README.md` and added to `.env.example`
- [ ] Schema changes ship as an EF Core migration that is safe to apply to a live database
- [ ] Commits are signed off (`git commit -s`)

## Notes for the reviewer

<!-- Anything worth a closer look, or deliberately left out. -->
