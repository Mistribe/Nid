import type { TFunction } from 'i18next';
import '@/locales/admin';

const DAYS = ['SUN', 'MON', 'TUE', 'WED', 'THU', 'FRI', 'SAT'];
const pad = (n: number) => String(n).padStart(2, '0');
const anyDay = (dow: string) => dow === '?' || dow === '*';

/** A job's French name; an unknown key is shown as itself. */
export function jobName(t: TFunction, key: string): { name: string; known: boolean } {
  const name = t(`admin.jobs.names.${key}`, { defaultValue: '' });
  return name ? { name, known: true } : { name: key, known: false };
}

export const jobDescription = (t: TFunction, key: string): string | undefined => t(`admin.jobs.descriptions.${key}`, { defaultValue: '' }) || undefined;
export const jobDependsOn = (t: TFunction, key: string): string | undefined => t(`admin.jobs.depends.${key}`, { defaultValue: '' }) || undefined;

/**
 * A Quartz cron (UTC, `s m h dom mon dow`) in words, in the reader's time zone, for the shapes Nid schedules (admin jobs design
 * §4.4); any other shape is shown as the cron itself. `today` places the UTC time on a date, so a weekly day shifts with it.
 */
export function scheduleInWords(t: TFunction, cron: string | null | undefined, today: Date = new Date()): string {
  if (!cron) return t('admin.jobs.schedule.on_demand');
  const fields = cron.trim().split(/\s+/);
  if (fields.length < 6 || fields[0] !== '0' || fields[4] !== '*') return cron;
  const [, min = '', hour = '', dom = '', , dow = ''] = fields;
  const minute = /^\d+$/.test(min) ? Number(min) : null;
  const hourOf = /^\d+$/.test(hour) ? Number(hour) : null;

  if (min === '*' && hour === '*' && dom === '*' && anyDay(dow)) return t('admin.jobs.schedule.every_minute');
  if (minute !== null && hour === '*' && dom === '*' && anyDay(dow))
    return minute === 0 ? t('admin.jobs.schedule.hourly') : t('admin.jobs.schedule.hourly_at', { minute });
  if (minute === null || hourOf === null) return cron;

  const at = new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate(), hourOf, minute));
  const time = `${pad(at.getHours())}:${pad(at.getMinutes())}`;
  // -1, 0 or +1: the local day of that moment against its UTC day.
  const shift = (((at.getDay() - at.getUTCDay()) % 7) + 10) % 7 - 3;
  if (dom === '*' && anyDay(dow)) return t('admin.jobs.schedule.daily', { time });
  if (dom !== '?') return cron;
  if (dow.toUpperCase() === 'MON-FRI' && shift === 0) return t('admin.jobs.schedule.weekdays', { time });
  const day = DAYS.indexOf(dow.toUpperCase());
  if (day < 0) return cron;
  return t('admin.jobs.schedule.weekly', { day: t(`admin.jobs.schedule.days.${(day + shift + 7) % 7}`, { defaultValue: '' }), time });
}
