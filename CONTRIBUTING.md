# Contributing to Nid

Thank you for your interest. Nid is a personal project, built for one French household and maintained in its spare
time: issues and pull requests are welcome, but please open an issue to discuss anything larger than a fix before you
write the code, so that nobody spends time on a change that will not land.

The development setup (Aspire, Docker, Node, the demo household) is in the [README](README.md#development).

## Never share real financial data

Not yours, not anyone's: no real amounts, balances, salaries, tax notices, contracts or account numbers, no real names,
addresses or birth dates, no screenshots of a real instance. This holds for issues, pull requests, comments, commits,
tests, fixtures and documentation alike. Describe the shape of a problem (« a savings account over its ceiling ») and
use invented figures, or the demo household (Alice, Bob and Léa, in [`deploy/local/demo/`](deploy/local/demo)).

## Issues

Use one of the templates: **Bug report** for a defect, **Feature request** for anything else. Write in English and fill
in every section. Check first that the issue does not already exist.

To report a vulnerability, do not open an issue: see [SECURITY.md](SECURITY.md).

## Pull requests

1. **One concern per pull request.** List follow-ups under « Notes for the reviewer » rather than folding them in.
2. **Branch name**: `<type>/<short-slug>` in kebab-case, with the issue number first when there is one:
   `feat/249-catalog-includes`, `fix/tax-negative-rental-income`. Types: `feat`, `fix`, `docs`, `refactor`, `test`, `ci`,
   `deps`, `chore`.
3. **Title**: short, imperative, no trailing period, the area first when it helps (`Property screens: …`). The title
   becomes the squash-merge commit, and it sets the next version (see below).
4. **Description**: the [pull request template](.github/pull_request_template.md), in English, every section filled
   in. Link the issue it resolves with `Closes #N`.
5. **Tests**: CI builds and checks a pull request but does **not** run the tests. Run the ones for the areas you change
   before opening it, and say so in the test plan:

   ```bash
   dotnet test --solution Nid.slnx --configuration Release --max-parallel-test-modules 1   # for src/ or tests/ (Docker required)
   pnpm --dir web check                                                                   # for web/
   ```

   A pull request that only changes documentation or workflows has nothing to run.
6. **Changelog**: a change the household would notice adds one file to [`web/changelog/`](web/changelog/README.md),
   with a line in English and one in French (ask in the pull request if you need help with the French). A change it
   would not notice (CI, refactoring, tests, docs) adds none; a maintainer then sets the `no-changelog` label.

Maintainers add the labels (type, area, size) and merge with a squash.

### Versioning

Every push to `main` is a release: CI computes the version from the commit messages since the last tag, builds and
tags it. A pull request title is a **patch** by default; `feat:` or `feat(area):` makes a **minor**; `!:` in the title
(`feat!: …`) or `BREAKING CHANGE:` in the description makes a **major**. These markers are matched anywhere in the
message, so do not write them unless the change deserves the bump.

## Code

- **Language**: code, comments, commit messages, pull requests and issues are in English. The interface is in French
  (`web/src/locales/`). The design documents in [`docs/`](docs) are in French.
- **Amounts are computed by the server.** The browser shows the server's figures and never does arithmetic on amounts
  (the `nid/no-amount-arithmetic` lint rule). Money is `decimal` on the server and a decimal string on the wire.
- **Modules own their data**: one PostgreSQL schema and one `DbContext` per module, and a module reaches another only
  through its `Contracts` project.
- **Web UI**: reach for a [shadcn/ui](https://ui.shadcn.com) component first (`pnpm dlx shadcn@latest add <name>` from
  `web/`), never a native `<select>`, `<dialog>`, `<table>`, date or checkbox input outside `components/ui/`, and theme
  tokens instead of Tailwind palette colours (`web/src/nativeControls.test.ts` checks both). New screens follow the page
  grammar of [`docs/frontend/design-system.md`](docs/frontend/design-system.md) §6.1, with
  `web/src/features/investments/EnvelopeDetailPage.tsx` as the reference.
- **Bundle budget**: the initial JavaScript stays under 200 kB gzip (`pnpm --dir web budget`); pages are lazy routes.
- **API contract**: after a change to the API, `dotnet build` rewrites `web/src/api/openapi.json`; run
  `pnpm --dir web gen:api` and commit both files.

The maintainers' complete working notes are in [`.claude/CLAUDE.md`](.claude/CLAUDE.md).

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE), like the rest of the
project.
