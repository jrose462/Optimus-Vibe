# Contributing

Pull requests are welcome.

- Build and test: `dotnet build OptimusVibe.sln` and `dotnet test tests/OptimusVibe.Tests`. CI runs both on Windows for every
  PR and must pass. Setup details: [docs/ci.md](docs/ci.md).
- Keep changes focused and add or update tests.
- Everything reaches `main` through a pull request, including the maintainer's own changes: work on a short-lived branch,
  give the PR a [Conventional Commits](https://www.conventionalcommits.org) title (`fix(app): ...`), and it is
  squash-merged once CI is green. GitHub rulesets enforce this; nobody can push to `main` directly. Releases are tags on
  `main`, and the release workflow refuses a commit that isn't on `main` or didn't pass CI.
- Bug reports from the app become public issues. Claude may triage them and open a `claude/*` fix PR. Low-risk fixes that pass
  CI and an independent review merge automatically; the maintainer reviews the rest. See [docs/pipeline.md](docs/pipeline.md).
- Per-user game presets and settings live in each user's own private config repo, not here. The only config in this repo is
  the generic starter in `seed/config/`.
- Do not commit game assets, screenshots showing game art, logos, or code you cannot license under MIT.
- Security issues: see [SECURITY.md](SECURITY.md). Behaviour: [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

By contributing you agree your contribution is licensed under the MIT License.
