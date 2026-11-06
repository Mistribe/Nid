# Nid

## Issues and pull requests

Issues use the templates in `.github/ISSUE_TEMPLATE/` (bug report, feature request).
Pull requests use `.github/pull_request_template.md`.

- Write the issue or PR body in English, filling every section of the matching template. Remove a test-plan line only when it does not apply.
- Title: short, imperative, no trailing period, same style as the commit subjects (`git log`). Subject area first when it helps, e.g. `Property screens: …`.
- Pick the template that fits: `bug_report.md` for a defect, `feature_request.md` for anything else.
- `gh issue create` and `gh pr create` do not apply templates on their own: read the template file and pass its filled-in content as `--body`.
- Link the issue a PR resolves with `Closes #N` in the Summary.
- Keep the PR to one concern; list follow-ups in « Notes for the reviewer » instead of folding them in.
- CI runs no test on a pull request: before opening one, run the tests of the areas it changes, `dotnet test --solution Nid.slnx --configuration Release --max-parallel-test-modules 1` for `src/` or `tests/`, `pnpm --dir web check` for `web/`, and fix what fails. A PR that only changes docs or workflows has nothing to run.
- Open a pull request as soon as a branch is pushed, without waiting to be asked.
- A PR the household would notice adds one `web/changelog/<slug>.json` (`{ "en": …, "fr": … }`, see its README): the
  version list behind the version number. The French lives there only, never in a commit message or a PR. A PR the
  household would not notice (CI, refactor, tests) gets the `no-changelog` label; the `changelog` workflow checks one or the other.

Branches are named `<type>/<short-slug>`, in kebab-case, with the issue number first when there is one: `feat/249-catalog-includes`,
`fix/tax-negative-rental-income`. The app's `claude/…` worktree branches are renamed (`git branch -m`) before the first push.
The prefix never changes the version: only the PR title does (see Versioning).

Every PR is created with its labels (`gh pr create --label … --label …`), never left bare:

| Branch prefix | Used for | Type label |
|---|---|---|
| `feat/` | a new capability | `enhancement` |
| `fix/` | a defect | `bug` |
| `docs/` | documentation only | `documentation` |
| `refactor/` | no change in behaviour | `technical` |
| `test/` | tests only | `technical` |
| `ci/` | workflows, build, images | `technical` |
| `deps/` | dependency updates | `technical` |
| `chore/` | anything else that is not a feature or a fix | `technical` |

On top of the type label:

- One area label per area the PR changes: `web` (`web/`), `backend` (`src/`), `mcp` (the assistant's MCP tools), `infra` (`deploy/`, `.github/`).
- One size label, the estimate of the work done: `Small` (one layer, up to a day), `Medium` (several layers, a few days, one PR),
  `Large` (a whole feature in one area, about a week), `Extra Large` (several areas or stages, several PRs).
- `no-changelog` when the household would not notice the PR (see above).
- `pdf` when the PR touches PDF import or attachment.

## Web UI

The web app uses shadcn/ui (`web/components.json`, components in `web/src/components/ui/`).

- Reach for a shadcn/ui component first (`pnpm dlx shadcn@latest add <name>` from `web/`) before writing one by hand: select, dialog, input, checkbox, tabs, tooltip, etc.
- Do not use a native `<select>` (or another browser widget with its own popup) where shadcn/ui has a component for it.
- Build a component by hand only when shadcn/ui has no equivalent; say so in the PR.
- `web/src/nativeControls.test.ts` fails on a native `<select>`, `<dialog>`, `<details>`, `<table>` or `type="date|checkbox|radio"` outside `components/ui/`, and on a Tailwind palette class (`text-amber-700`, `bg-blue-50`…): use a theme token (`text-status-warning-text`, `bg-status-warning/10`…).

Every screen follows one page grammar (`docs/frontend/design-system.md` §6.1, `docs/frontend/screens.md`); build new screens from it, with `web/src/features/investments/EnvelopeDetailPage.tsx` as the reference:

- Page parts in `web/src/components/page/`: `PageHeader` (breadcrumb, title, actions), `StatLine` (key figures inline, no big cards), `DetailTabs` + `useDetailTab` (`?tab=`, first tab dropped from the URL), `SortableTable` (lists), `RowMenu` / `RowActions` (a row's `⋯` menu, delete confirmation, « Recharger »), `StepForm` (long creation forms), `Section` (a detail page's titled region, no card), `SectionCard` (an overview's titled card: dashboard, income).
- Fields in `web/src/components/form/`: `Field` (label, hint, error wiring), `SelectField`, `ComboboxField` (searchable, long lists), `CurrencyField`, `DateField` (`jj/mm/aaaa`, `max` for past-only dates), `Segmented`. Dialogs: `FormDialog`, `ModalDialog`, `ConfirmAction`.
- Lists are dense tables with an owner column (`OwnerChips`); details show figures inline, then tabs, entry forms in dialogs; creation forms are their own page, in steps when long, rare fields folded.
- Radix `TabsContent` unmounts inactive tabs: state the page hands into a tab must be consumed once.
- Colours come from theme tokens only; the default theme is `ardoise` (« Confiance », blue accent); chart and status colours are fixed (`docs/frontend/dataviz.md`).
- Never compute an amount in the browser (`nid/no-amount-arithmetic`): show the server's figures; `toPlot` only sizes marks or sorts.
- Filters and search are applied on the server, never in the browser. A list that can grow takes its filters, its search and `page` as query parameters. It answers one page, the total, and the options its filters offer. The store applies them in the SQL query, read rights included, rather than loading everything and filtering in memory. The page keeps them in the URL. `GET /api/v1/documents` and `DocumentsPage` are the reference (#420).
- Initial JS stays under 200 kB gzip (`pnpm --dir web budget`): pages are lazy routes, heavy parts (cmdk, calendar, confirmation dialog) load lazily.
- Tests: `pick` (`web/src/test/controls.ts`) for selects and comboboxes; `pick` / `fillDate` (`web/e2e/controls.ts`) in Playwright; a Radix modal makes the page behind inert (`hidden: true`).

## Versioning

Every push to `main` is a release: the `images` job of `.github/workflows/ci.yml` computes the version with
[`PaulHatch/semantic-version`](https://github.com/marketplace/actions/git-semantic-version), builds the API with it
(`-p:Version`), pushes the images tagged with it and pushes the git tag `vX.Y.Z`. Nothing to do by hand.

The bump comes from the commit messages since the last `vX.Y.Z` tag, so it comes from the PR title (the squash-merge subject) and
body:

- Patch by default, whatever the title says (`Images: …`, `Property screens: …`).
- Minor: `feat:` or `feat(area):` in the title, e.g. `feat(income): …`.
- Major: `!:` in the title (`feat!: …`) or `BREAKING CHANGE:` in the body.
- Do not write these markers in a title or body that does not deserve the bump: they are matched anywhere in the message.
- Do not create `v*` tags by hand: the next version is computed from the latest one.
