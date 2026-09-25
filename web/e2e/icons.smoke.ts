import { expect, test } from '@playwright/test';
import { signIn } from './session';

// SkiaSharp in the nid-api image: the catalog's icon pass (1C2c) decodes and re-encodes; a stored icon proves it ran.
test('an icon the catalog stored is served as WebP', async ({ page }) => {
  test.setTimeout(240_000);
  await signIn(page, 'alice', '/subscriptions');
  let iconUrl: string | null = null;
  await expect.poll(async () => {
    const providers = (await (await page.request.get('/api/v1/subscriptions/providers')).json()) as { iconUrl?: string | null }[];
    iconUrl = providers.find((p) => p.iconUrl)?.iconUrl ?? null;
    return iconUrl;
  }, { timeout: 180_000, intervals: [5_000] }).not.toBeNull();
  const icon = await page.request.get(iconUrl!);
  expect(icon.headers()['content-type']).toBe('image/webp');
});
