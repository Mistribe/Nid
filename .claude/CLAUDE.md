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
