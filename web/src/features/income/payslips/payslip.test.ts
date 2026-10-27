import type { TFunction } from 'i18next';
import { describe, expect, it, vi } from 'vitest';
import i18n from '@/app/i18n';
import { uploadDocument } from '../../documents/api';
import { link, summary } from '../../documents/testData';
import { attachTarget, coverage, isMissing, linksOnSource, monthOfFileName, oneOffTitle, payslipTitle } from './payslip';

const t = i18n.t.bind(i18n) as unknown as TFunction;
const SRC = 'src-1', PH = 'ph-1';
const phases = new Set([PH]);
const slip = (id: string, documentDate: string | null, kind: string, targetId: string, role = 'payslip') =>
  summary({ id, documentDate, links: [link({ targetKind: kind, targetId, role })] });

describe('monthOfFileName', () => {
  it.each([
    ['bulletin_2026-09.pdf', '2026-09'], ['2026_09 paie.pdf', '2026-09'], ['202609.pdf', '2026-09'], ['09-2026.pdf', '2026-09'],
    ['Paie 092026.pdf', '2026-09'], ['Bulletin septembre 2026.pdf', '2026-09'], ['BULLETIN SEPT 2026.PDF', '2026-09'],
    ['fevrier 2025.pdf', '2025-02'], ['février 2025.pdf', '2025-02'], ['août 2024.pdf', '2024-08'],
    ['releve_2026-13.pdf', null], ['scan_1202609.pdf', null], ['facture.pdf', null],
  ])('%s -> %s', (name, month) => expect(monthOfFileName(name)).toBe(month));
});

describe('coverage', () => {
  it('keeps the source payslips by document month and tracks the undated and the oldest', () => {
    const docs = [
      slip('a', '2026-09-30', 'income.phase', PH), slip('b', '2026-08-28', 'core.item', SRC), slip('c', '2026-07-01', 'income.phase', PH, 'other'),
      slip('d', '2026-06-01', 'income.one_off', 'oo-1'), slip('e', null, 'income.phase', PH),
    ];
    const { byMonth, undated, oldest } = coverage(docs, SRC, phases);
    expect([...byMonth.keys()].sort()).toEqual(['2026-08', '2026-09']);
    expect(undated.map((d) => d.id)).toEqual(['e']);
    expect(oldest).toBe('2026-08');
  });
  it('counts a payslip dated 30 September for September even if the payment falls on 1 October', () => {
    expect(coverage([slip('a', '2026-09-30', 'income.phase', PH)], SRC, phases).byMonth.has('2026-09')).toBe(true);
  });
});

describe('isMissing', () => {
  const base = { salary: true, pastPayment: true, oldest: '2026-01', covered: false };
  it('is true for an uncovered past salary month since the oldest payslip', () => expect(isMissing('2026-03', base)).toBe(true));
  it.each([{ salary: false }, { covered: true }, { pastPayment: false }, { oldest: null }])('is false with %o', (over) =>
    expect(isMissing('2026-03', { ...base, ...over })).toBe(false));
  it('is false before the oldest month', () => expect(isMissing('2025-12', base)).toBe(false));
});

describe('attachTarget', () => {
  it('takes the earliest payment', () =>
    expect(attachTarget([{ phaseId: 'p2', on: '2026-09-15' }, { phaseId: 'p1', on: '2026-09-01' }], '2026-10-27')).toEqual({ phaseId: 'p1', date: '2026-09-01' }));
  it('caps a payment later this month to today', () =>
    expect(attachTarget([{ phaseId: 'p1', on: '2026-10-30' }], '2026-10-27')).toEqual({ phaseId: 'p1', date: '2026-10-27' }));
  it('refuses a later month and an empty list', () => {
    expect(attachTarget([{ phaseId: 'p1', on: '2026-11-30' }], '2026-10-27')).toBeNull();
    expect(attachTarget([], '2026-10-27')).toBeNull();
  });
});

it('linksOnSource keeps the other links out', () => {
  const doc = summary({ links: [
    link({ targetKind: 'income.phase', targetId: PH }), link({ targetKind: 'core.item', targetId: SRC, role: 'other' }),
    link({ targetKind: 'income.phase', targetId: 'ph-other' }), link({ targetKind: 'core.household', targetId: 'h' }),
  ] });
  expect(linksOnSource(doc, SRC, phases).map((l) => l.targetId)).toEqual([PH, SRC]);
});

describe('titles', () => {
  it('names the payslip after its month', () => expect(payslipTitle(t, '2026-09')).toBe('Bulletin de paie septembre 2026'));
  it('prefers the one-off note', () => expect(oneOffTitle(t, { note: 'Solde de tout compte', occurredOn: '2026-06-30' })).toBe('Solde de tout compte'));
  it('falls back to the date', () => expect(oneOffTitle(t, { occurredOn: '2026-06-30' })).toBe('Revenu exceptionnel 30/06/2026'));
});

it('uploadDocument sends its fields with the file', async () => {
  const POST = vi.fn().mockResolvedValue({ data: { id: 'x' } });
  const fields = { targetKind: 'income.phase', targetId: PH, role: 'payslip', date: '2026-09-01', title: 'T' };
  await uploadDocument({ POST } as never, new File(['x'], 'a.pdf'), fields);
  const body = (POST.mock.calls[0] as [string, { body: FormData }])[1].body;
  expect(Object.fromEntries([...body.entries()].filter(([k]) => k !== 'file'))).toEqual(fields);
  expect(body.get('file')).toBeInstanceOf(File);
});
