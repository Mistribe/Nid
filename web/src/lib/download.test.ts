import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { downloadBlob } from '@/lib/download';

describe('downloadBlob', () => {
  const revoke = vi.fn();
  beforeEach(() => {
    vi.useFakeTimers();
    revoke.mockClear();
    URL.createObjectURL = vi.fn(() => 'blob:nid/1');
    URL.revokeObjectURL = revoke;
  });
  afterEach(() => vi.useRealTimers());

  it('clicks a download link named after the file', () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    downloadBlob(new Blob(['{}']), 'nid-2026-09-26.nid.json');
    expect(click).toHaveBeenCalledTimes(1);
    expect((click.mock.contexts[0] as HTMLAnchorElement).download).toBe('nid-2026-09-26.nid.json');
    expect(document.querySelector('a[download]')).toBeNull();
    click.mockRestore();
  });

  // Final review: some Safari and Firefox versions cancel a download whose URL is revoked on the next tick.
  it('keeps the object URL long enough for every browser to start the download', () => {
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    downloadBlob(new Blob(['{}']), 'nid.nid.json');
    vi.advanceTimersByTime(30_000);
    expect(revoke).not.toHaveBeenCalled();
    vi.advanceTimersByTime(10_000);
    expect(revoke).toHaveBeenCalledWith('blob:nid/1');
  });
});
