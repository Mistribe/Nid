import { afterEach, describe, expect, it, vi } from 'vitest';
import { session } from '@/test/importServer';
import { downloadReport, reportFile } from './report';

describe('reportFile', () => {
  it('is the session and its report as indented JSON, named after the session', () => {
    const report = { state: 'committed', sections: { subscriptions: { created: 1, unchanged: 0, conflict: 0, replaced: 0 } }, issues: [] };
    const file = reportFile(session({ state: 'committed', report }));
    expect(file.name).toBe('nid-import-0192aaaa-0000-7000-8000-000000000001.json');
    expect(JSON.parse(file.content)).toEqual({
      id: '0192aaaa-0000-7000-8000-000000000001', state: 'committed', createdAt: '2026-09-24T10:00:00+00:00', report, audit: null,
    });
    expect(file.content.endsWith('\n')).toBe(true);
  });
});

describe('downloadReport', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('revokes the file’s address only after the click has been handled', () => {
    vi.useFakeTimers();
    vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:report');
    const revoke = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    downloadReport(session({ state: 'committed' }));
    expect(click).toHaveBeenCalledOnce();
    expect(revoke).not.toHaveBeenCalled(); // some browsers read the address after `click()` returns
    vi.runAllTimers();
    expect(revoke).toHaveBeenCalledWith('blob:report');
  });
});
