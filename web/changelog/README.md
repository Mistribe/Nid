# Changelog

The version list the web app shows on a click on its version number. A pull request that changes something the
household sees adds one file here, named after the change (`tax-page-scroll.json`), with one line per language:

```json
{
  "en": "The tax page keeps its scroll position when you change view.",
  "fr": "La page Impôts garde sa position quand vous changez de vue."
}
```

The `changelog` workflow (`.github/workflows/changelog.yml`) fails a pull request that changes `src/` or `web/src/`
(tests aside) without a file here; a change the household does not see carries the `no-changelog` label instead.

Say what changes for them, not how. The files of the versions released before this list name their version
(`"version": "0.13.0"`) and start with it (`0.13.0-goals.json`); a new file names none. A change they do not see (CI, refactor, docs) adds no file.

Nothing else to do: `scripts/changelog.mjs` gives each file the version of the first release that holds the commit
which added it, and the `build` job of `ci.yml` writes the result to `public/changelog.json` before building the image.
`scripts/changelog.test.js` checks every file has a non-empty line in each language the app knows.
