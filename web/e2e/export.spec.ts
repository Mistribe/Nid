import { readFile } from 'node:fs/promises';
import { expect, test, type Download, type Page } from '@playwright/test';
import { signIn } from './session';

/**
 * The other journeys run in parallel and write (subscriptions.spec adds a subscription): the export may see its data
 * change between its first and last read. The screen then downloads nothing and offers to start again, as designed;
 * the test starts again, three times at most.
 */
async function exportFile(page: Page): Promise<Download> {
  for (let attempt = 0; attempt < 3; attempt++) {
    const downloading = page.waitForEvent('download', { timeout: 30_000 });
    downloading.catch(() => {}); // abandoned when the data changed
    await page.getByRole('button', { name: attempt === 0 ? 'Exporter' : 'Relancer' }).click();
    const changed = page.getByText("Vos données ont changé pendant l'export").waitFor({ timeout: 30_000 }).then(() => null);
    changed.catch(() => {}); // left pending when the download wins, it rejects later on its timeout or at teardown
    const download = await Promise.race([downloading, changed]);
    if (download) return download;
  }
  throw new Error('the data kept changing during three exports');
}

// 1E4 design §4: the export is a file Nid reads back. It is given to the import assistant's local check, never sent:
// re-importing into the instance it came from is not guaranteed (import-format.md §10).
test('alice exports what she sees, and the import assistant reads the file back', async ({ page }) => {
  await signIn(page, 'alice', '/export');
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
  await expect(page.getByRole('alert')).toHaveCount(0);
});
