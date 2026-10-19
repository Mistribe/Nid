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

## Web UI

The web app uses shadcn/ui (`web/components.json`, components in `web/src/components/ui/`).

- Reach for a shadcn/ui component first (`pnpm dlx shadcn@latest add <name>` from `web/`) before writing one by hand: select, dialog, input, checkbox, tabs, tooltip, etc.
- Do not use a native `<select>` (or another browser widget with its own popup) where shadcn/ui has a component for it.
- Build a component by hand only when shadcn/ui has no equivalent; say so in the PR.
- `web/src/nativeControls.test.ts` fails on a native `<select>`, `<dialog>`, `<details>`, `<table>` or `type="date|checkbox|radio"` outside `components/ui/`.

Every screen follows one page grammar (`docs/frontend/design-system.md` §6.1, `docs/frontend/screens.md`); build new screens from it, with `web/src/features/investments/EnvelopeDetailPage.tsx` as the reference:

- Page parts in `web/src/components/page/`: `PageHeader` (breadcrumb, title, actions), `StatLine` (key figures inline, no big cards), `DetailTabs` + `useDetailTab` (`?tab=`, first tab dropped from the URL), `SortableTable` (lists), `RowMenu` / `RowActions` (a row's `⋯` menu, delete confirmation, « Recharger »), `StepForm` (long creation forms).
- Fields in `web/src/components/form/`: `Field` (label, hint, error wiring), `SelectField`, `ComboboxField` (searchable, long lists), `CurrencyField`, `DateField` (`jj/mm/aaaa`, `max` for past-only dates), `Segmented`. Dialogs: `FormDialog`, `ModalDialog`, `ConfirmAction`.
- Lists are dense tables with an owner column (`OwnerChips`); details show figures inline, then tabs, entry forms in dialogs; creation forms are their own page, in steps when long, rare fields folded.
- Radix `TabsContent` unmounts inactive tabs: state the page hands into a tab must be consumed once.
- Colours come from theme tokens only; the default theme is `ardoise` (« Confiance », blue accent); chart and status colours are fixed (`docs/frontend/dataviz.md`).
- Never compute an amount in the browser (`nid/no-amount-arithmetic`): show the server's figures; `toPlot` only sizes marks or sorts.
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
