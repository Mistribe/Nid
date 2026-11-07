// The version list shown on a click on the version (src/app/Changelog.tsx), written to public/changelog.json by the
// build job of ci.yml before the image is built. Each change the household sees has its own file in web/changelog/,
// { "en": "…", "fr": "…" }; its version is the first vX.Y.Z tag holding the commit that added the file, or the version
// being built when no tag holds it yet. One file per change, so two pull requests never conflict on it. A file written
// after its release (the history from before this list) names it: "version": "0.13.0".
// Usage: node scripts/changelog.mjs [version being built] > public/changelog.json
import { execFileSync } from 'node:child_process';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const LANGUAGES = ['en', 'fr'];
const SEMVER = /^v(\d+)\.(\d+)\.(\d+)$/;
const VERSION = /^\d+\.\d+\.\d+$/;

/** The problems of one change file: each language a non-empty line of text, and a release's version if it names one. */
export function problems(entry) {
  if (typeof entry !== 'object' || entry === null || Array.isArray(entry)) return ['not an object'];
  const extra = Object.keys(entry).filter((key) => !LANGUAGES.includes(key) && key !== 'version').map((key) => `unknown language « ${key} »`);
  if ('version' in entry && !(typeof entry.version === 'string' && VERSION.test(entry.version))) extra.push('« version » is not X.Y.Z');
  const missing = LANGUAGES.filter((lang) => typeof entry[lang] !== 'string' || entry[lang].trim() === '' || entry[lang].includes('\n'))
    .map((lang) => `« ${lang} » is not one line of text`);
  return [...extra, ...missing];
}

/** One entry per version, newest first, its changes in file order; `changes` are { version, date, text }. */
export function changelog(changes) {
  const byVersion = new Map();
  for (const { version, date, text } of changes) {
    const entry = byVersion.get(version) ?? { version, date, changes: [] };
    entry.changes.push(text);
    byVersion.set(version, entry);
  }
  const key = (version) => version.split(/[.-]/).slice(0, 3).map(Number);
  return [...byVersion.values()].sort((a, b) => {
    const [x, y] = [key(a.version), key(b.version)];
    return y[0] - x[0] || y[1] - x[1] || y[2] - x[2];
  });
}

function git(...args) {
  return execFileSync('git', args, { encoding: 'utf8' }).trim();
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const current = process.argv[2] ?? 'dev';
  const dir = fileURLToPath(new URL('../changelog/', import.meta.url));
  const changes = readdirSync(dir).filter((name) => name.endsWith('.json')).sort().map((name) => {
    const path = join(dir, name);
    const { version, ...text } = JSON.parse(readFileSync(path, 'utf8'));
    if (version) {
      if (git('tag', '--list', `v${version}`) === '') throw new Error(`${name}: no tag v${version}`);
      return { version, date: git('log', '-1', '--format=%cs', `v${version}`), text };
    }
    // The commit that added the file (the oldest, should it have been removed and added again), along main.
    const added = git('log', '--first-parent', '--diff-filter=A', '--format=%H', '--', path).split('\n').filter(Boolean).at(-1);
    const tag = added ? git('tag', '--contains', added, '--list', 'v*', '--sort=v:refname').split('\n').find((t) => SEMVER.test(t)) : undefined;
    return {
      version: tag ? tag.slice(1) : current,
      date: git('log', '-1', '--format=%cs', tag ?? 'HEAD'),
      text,
    };
  });
  process.stdout.write(`${JSON.stringify(changelog(changes))}\n`);
}
