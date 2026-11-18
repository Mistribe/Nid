import { afterEach, describe, expect, it, vi } from 'vitest';
import { announceNewVersion, reloadOnMissingChunk } from './newBuild';

const toastInfo = vi.hoisted(() => vi.fn<(message: string, options: { action: { onClick: () => void } }) => void>());
vi.mock('sonner', () => ({ toast: { info: toastInfo } }));

afterEach(() => { sessionStorage.clear(); toastInfo.mockReset(); });

describe('announceNewVersion', () => {
  it('offers a reload that stays until it is used', async () => {
    const location = { reload: vi.fn() };

    await announceNewVersion(location);

    const [message, options] = toastInfo.mock.calls[0]!;
    expect(message).toBe('Une nouvelle version de Nid est disponible.');
    expect(options).toMatchObject({ id: 'new-version', duration: Infinity, action: { label: 'Recharger' } });
    options.action.onClick();
    expect(location.reload).toHaveBeenCalledOnce();
  });
});

describe('reloadOnMissingChunk', () => {
  it('reloads once per build', () => {
    const location = { reload: vi.fn() };
    const first = new Event('vite:preloadError', { cancelable: true });

    reloadOnMissingChunk(first, '1.3.0', () => sessionStorage, location);
    reloadOnMissingChunk(new Event('vite:preloadError', { cancelable: true }), '1.3.0', () => sessionStorage, location);

    expect(first.defaultPrevented).toBe(true);
    expect(location.reload).toHaveBeenCalledOnce();
    reloadOnMissingChunk(new Event('vite:preloadError', { cancelable: true }), '1.4.0', () => sessionStorage, location);
    expect(location.reload).toHaveBeenCalledTimes(2);
  });

  it('lets the error show when the browser keeps no storage', () => {
    const location = { reload: vi.fn() };
    const event = new Event('vite:preloadError', { cancelable: true });

    reloadOnMissingChunk(event, '1.3.0', () => { throw new DOMException('denied', 'SecurityError'); }, location);

    expect(event.defaultPrevented).toBe(false);
    expect(location.reload).not.toHaveBeenCalled();
  });
});
