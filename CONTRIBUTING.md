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
