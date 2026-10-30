## Summary

<!-- What changes and why, in one to three sentences. Closes #… if it resolves an issue. -->

## Changes

-

## Test plan

<!-- What you ran and what you saw. Delete the lines that do not apply. -->

- [ ] `dotnet test Nid.slnx --configuration Release -m:1`
- [ ] `pnpm --dir web check` (and `check:api` if the API contract changed)
- [ ] Checked in the running app (`aspire run`)
- [ ] `web/changelog/<slug>.json` added, or the household sees no change (label `no-changelog`)

## Notes for the reviewer

<!-- Risks, migrations, follow-ups, anything not obvious from the diff. -->

## Estimate

<!-- Small, Medium, Large or Extra Large; add the label of the same name. Small: one layer, up to a day. Medium: several layers, a few days, one PR. Large: a whole feature in one area, a spec first, about a week. Extra Large: several areas or stages, several PRs. -->
