# Contributing to Aetherium Forge

Thanks for your interest. The design lives in [IMPLEMENTATION_GUIDE.md](IMPLEMENTATION_GUIDE.md); read it before starting larger changes.

## Branch model

- `main`: stable releases only. Never commit directly.
- `develop`: integration branch. Open pull requests against `develop`.
- `feature/<short-name>` and `fix/<short-name>`: branch from `develop`.
- `hotfix/<short-name>`: branch from `main`, merge into both `main` and `develop`.

### Merging

Always integrate with a **true merge commit** so history stays traceable:

```powershell
git merge --no-ff feature/my-change
```

On GitHub, complete pull requests with **Create a merge commit**. Do not squash, rebase-merge or cherry-pick as the integration method.

## CI and releases

- `CI` workflow: builds and tests every push and pull request on `main` and `develop`. It produces no packages.
- `Release` workflow: runs only on a version tag `vX.Y.Z` (for example `v0.1.0`) whose commit is on `main`. It builds, tests, packages the app and creates a draft GitHub release.
- The version comes from the tag through [MinVer](https://github.com/adamralph/minver). Never hard-code `<Version>`.

To release, merge `develop` into `main` with a merge-commit PR, then tag the merge commit on `main`:

```powershell
git switch main
git pull
git tag -a v0.1.0 -m "v0.1.0"
git push origin v0.1.0
```

## Commit messages

Use [Conventional Commits](https://www.conventionalcommits.org/):

```
feat: add BSA writer
fix: correct TES4 hash for two-letter names
docs: clarify gap-set rules
```

## Code standards

- .NET 10, C#, nullable reference types enabled, warnings treated as errors.
- Follow [.editorconfig](.editorconfig): 4-space indent, braces always, `_camelCase` private fields, `I`-prefixed interfaces, `Async` suffix on async methods.
- XML documentation on public APIs.
- Unit tests (xUnit, Arrange-Act-Assert) for new logic in `AetheriumForge.Core`.

## What not to commit

- Game files or anything extracted from them (`*.dds`, `*.bsa`, `*.esp`, …).
- ONNX models or third-party binaries (`texconv.exe`): they are fetched by scripts.
- Personal paths, credentials or machine-specific settings.

## Reporting issues

Include your Windows version, GPU, game version (for example 1.6.1170), store (Steam/GOG/Epic) and mod manager, plus the log file from the run.
