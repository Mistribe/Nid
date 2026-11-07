import { readFile } from 'node:fs/promises';
import { expect, test, type Download, type Page } from '@playwright/test';
import { signIn } from './session';

/**
 * The « quiet » project runs this journey after the others (playwright.config.ts), but a journey run alongside it (`--no-deps`)
 * may change its data between its first and last read. The screen then downloads nothing and offers to start again, as
 * designed; the test starts again, six times at most.
 */
async function exportFile(page: Page): Promise<Download> {
  for (let attempt = 0; attempt < 6; attempt++) {
    const downloading = page.waitForEvent('download', { timeout: 30_000 });
    downloading.catch(() => {}); // abandoned when the data changed
    await page.getByRole('button', { name: attempt === 0 ? 'Exporter' : 'Relancer' }).click();
    const changed = page.getByText("Vos données ont changé pendant l'export").waitFor({ timeout: 30_000 }).then(() => null);
    changed.catch(() => {}); // left pending when the download wins, it rejects later on its timeout or at teardown
    const download = await Promise.race([downloading, changed]);
    if (download) return download;
  }
  throw new Error('the data kept changing during six exports');
}

/**
 * On a fresh base the export may run before any other journey saved something: an empty file, which the import
 * assistant refuses ("Le fichier ne contient aucune entité"). The journey brings its own subscription.
 */
async function addSubscription(page: Page): Promise<void> {
  const session = (await (await page.request.get('/auth/session')).json()) as { csrf: string; person: { id: string } };
  const response = await page.request.post('/api/v1/subscriptions', {
    headers: { 'X-Nid-Csrf': session.csrf },
    data: {
      displayName: `Export E2E ${Date.now().toString(36)}`, visibility: 'shared',
      owners: [{ personId: session.person.id, shareBp: 10000 }], autoRenew: true,
    },
  });
  expect(response.status()).toBe(201);
}

// 1E4 design §4: the export is a file Nid reads back. It is given to the import assistant's local check, never sent:
// re-importing into the instance it came from is not guaranteed (import-format.md §10).
test('alice exports what she sees, and the import assistant reads the file back', async ({ page }) => {
  await signIn(page, 'alice', '/export');
  await addSubscription(page);
  await expect(page.getByRole('heading', { level: 1, name: 'Export' })).toBeVisible();

  const download = await exportFile(page);
  const done = page.getByText(/^Export terminé : \d+ éléments?\.$/);
  await expect(done).toBeVisible();
  const announced = Number(/\d+/.exec((await done.textContent())!)![0]);

  expect(download.suggestedFilename()).toMatch(/^nid-\d{4}-\d{2}-\d{2}\.nid\.json$/);
  const text = await readFile(await download.path(), 'utf8');
  const file = JSON.parse(text) as Record<string, unknown>;
  expect(file.format).toBe('nid.data');
  expect(file.version).toBe(1);
  expect(Object.keys(file).slice(0, 3)).toEqual(['format', 'version', 'exported_at']);
  // The file holds what the screen counted: one state of the data, whatever the parallel journeys wrote since.
  const sections = Object.entries(file).filter((entry): entry is [string, unknown[]] => Array.isArray(entry[1]));
  expect(sections.map(([name]) => name)).toContain('subscriptions');
  expect(sections.reduce((sum, [, list]) => sum + list.length, 0)).toBe(announced);

  await page.goto('/import');
  await page.getByLabel('Choisir un fichier').setInputFiles({
    name: download.suggestedFilename(), mimeType: 'application/json', buffer: Buffer.from(text),
  });
  await expect(page.getByText(/^Lots à envoyer : \d+\.$/)).toBeVisible();
  // No error from the import; the shell's banner (an important notification other journeys raised) is not the import's.
  await expect(page.getByRole('main').getByRole('alert')).toHaveCount(0);
});
