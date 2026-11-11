# Contributing to momos

momos is dual-licensed (AGPL-3.0 and a commercial license), so a pull request can only be merged
once its author has signed the contributor license agreement. See [CLA.md](CLA.md): signing is one
file added to your own pull request (`.github/cla/v1/<your-lowercase-github-login>.md`), and the
`CLA` check on the pull request tells you if it is missing.

## Building and testing

```bash
dotnet build
dotnet test
```

The test suites that start a database or a container need Docker. Formatting is enforced:
`dotnet format --verify-no-changes` must pass, and files use LF line endings.

## What fits

momos never changes the repository it inspects and asks nothing of it; read
[docs/scope.md](docs/scope.md) and [docs/architecture.md](docs/architecture.md) before proposing a
new capability. Open an issue first for anything that changes a public API, adds a dependency, or
changes the data schema.
