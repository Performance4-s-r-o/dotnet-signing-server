# Contributing

Thanks for considering a contribution. This document covers what you need to
get a change merged.

## Licensing of contributions

This project is licensed under **AGPL-3.0**. By submitting a pull request you
agree that your contribution is licensed under the same terms (inbound =
outbound).

Sign off every commit to certify you have the right to submit the work under
that license — this is the [Developer Certificate of Origin](https://developercertificate.org):

```bash
git commit -s -m "fix(signing): ..."
```

`-s` appends a `Signed-off-by:` trailer using your `git config user.name` and
`user.email`.

## Getting set up

```bash
dotnet restore
dotnet build
dotnet test tests/DotNetSigningServer.Tests/DotNetSigningServer.Tests.csproj
```

Running the app locally needs Docker: with `UseLocalDb: true` the server starts
a throwaway PostgreSQL container through Testcontainers. See the
[README](README.md) for configuration.

## Branches and commits

Branch off `main`, naming the branch for the work: `fix/tsa-timeout`,
`feat/pdfa-3b`, `chore/bump-itext`.

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org):

```
feat(templates): support right-aligned table columns
fix(signing): honour DisableTsa when a default TSA is configured
chore(deps): bump Stripe.net to 46.1.0
docs(readme): document the seal configuration
```

Keep the subject under ~72 characters and explain *why* in the body when it is
not obvious from the diff.

## Pull requests

- One logical change per PR. Split unrelated refactors out.
- Add or update tests for behaviour you change. The suite must stay green.
- Update the README when you add configuration or endpoints.
- Fill in the PR template — reviewers rely on it.

CI runs build, tests and a formatting check on every PR. A PR needs a passing
build and one approval before it can be merged; merges are squashed.

## User-facing strings

Never hard-code text shown to users. Every string goes through the localisation
resources in `Resources/SharedStrings.*.resx`, and **EN and CS must both be
updated in the same change**. DE and ES are maintained on a best-effort basis —
add the key there too, even if the value is copied from EN for now.

## Database changes

Schema changes ship as EF Core migrations against the `dotnet_signing` schema:

```bash
dotnet ef migrations add DescriptiveName -p dotnet-signing-server.csproj
```

Commit the generated migration together with the code that needs it. Migrations
run automatically at startup, so they must be safe to apply to a live database:
prefer additive changes, and avoid destructive operations in the same release
that stops using a column.

## Code style

Match the surrounding code. The project uses standard .NET conventions and
nullable reference types are enabled. Before pushing:

```bash
dotnet format dotnet-signing-server.sln --verify-no-changes
```

## Security issues

Do not open a public issue or PR for a vulnerability. Follow
[SECURITY.md](SECURITY.md) instead.
