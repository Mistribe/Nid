import { describe, expect, it } from 'vitest';
import { session } from '@/test/importServer';
import { reportFile } from './report';

describe('reportFile', () => {
  it('is the session and its report as indented JSON, named after the session', () => {
    const report = { state: 'committed', sections: { subscriptions: { created: 1, unchanged: 0, conflict: 0 } }, issues: [] };
    const file = reportFile(session({ state: 'committed', report }));
    expect(file.name).toBe('nid-import-0192aaaa-0000-7000-8000-000000000001.json');
    expect(JSON.parse(file.content)).toEqual({
      id: '0192aaaa-0000-7000-8000-000000000001', state: 'committed', createdAt: '2026-09-24T10:00:00+00:00', report,
    });
    expect(file.content.endsWith('\n')).toBe(true);
  });
});
