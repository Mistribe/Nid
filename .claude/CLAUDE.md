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
