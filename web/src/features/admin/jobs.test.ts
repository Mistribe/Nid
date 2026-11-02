import { afterEach, describe, expect, it, vi } from 'vitest';
import i18n from '@/app/i18n';
import '@/locales/admin';
import { jobName, scheduleInWords } from './jobs';

const t = i18n.t.bind(i18n);
const today = new Date('2026-11-02T12:00:00Z');

/** The local getters read `hours` east of UTC (vitest workers ignore process.env.TZ). */
function stubZone(hours: number) {
  const shifted = (d: Date) => new Date(d.getTime() + hours * 3_600_000);
  vi.spyOn(Date.prototype, 'getHours').mockImplementation(function (this: Date) { return shifted(this).getUTCHours(); });
  vi.spyOn(Date.prototype, 'getMinutes').mockImplementation(function (this: Date) { return shifted(this).getUTCMinutes(); });
  vi.spyOn(Date.prototype, 'getDay').mockImplementation(function (this: Date) { return shifted(this).getUTCDay(); });
}

describe('scheduleInWords', () => {
  afterEach(() => vi.restoreAllMocks());

  it('says the shapes in use, in local time', () => {
    stubZone(1);
    expect(scheduleInWords(t, '0 * * * * ?', today)).toBe('Chaque minute');
    expect(scheduleInWords(t, '0 0 * * * ?', today)).toBe('Chaque heure');
    expect(scheduleInWords(t, '0 15 * * * ?', today)).toBe('Chaque heure à la minute 15');
    expect(scheduleInWords(t, '0 30 4 * * ?', today)).toBe('Chaque jour à 05:30');
    expect(scheduleInWords(t, '0 0 7 ? * MON', today)).toBe('Chaque lundi à 08:00');
    expect(scheduleInWords(t, '0 0 21 ? * MON-FRI', today)).toBe('Du lundi au vendredi à 22:00');
  });

  it('moves a weekly day with a time that crosses midnight', () => {
    stubZone(1);
    expect(scheduleInWords(t, '0 0 23 ? * SUN', today)).toBe('Chaque lundi à 00:00');
  });

  it('says « À la demande » without a cron, and the cron itself for another shape', () => {
    expect(scheduleInWords(t, null, today)).toBe('À la demande');
    expect(scheduleInWords(t, '0 0/5 * * * ?', today)).toBe('0 0/5 * * * ?');
    expect(scheduleInWords(t, '0 0 23 ? * MON-FRI', today)).toMatch(/23|00/);
  });
});

describe('jobName', () => {
  it('names a known job in French and falls back to the key', () => {
    expect(jobName(t, 'bank-sync-nightly')).toEqual({ name: 'Synchronisation bancaire', known: true });
    expect(jobName(t, 'unknown-job')).toEqual({ name: 'unknown-job', known: false });
  });
});
