import type { TFunction } from 'i18next';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { toProblem } from '@/api/errors';
import '@/locales/childcare';

/** A decimal string as Pajemploi takes it: `626.37` → `626,37`. Text only, nothing computed. */
export const frenchNumber = (value: string) => value.replace('.', ',');

/** `2025-09` → « septembre 2025 ». */
export const monthTitle = (month: string) =>
  new Intl.DateTimeFormat('fr-FR', { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(new Date(`${month}-01T00:00:00Z`));

/** `2025-09-08` → « lundi 8 septembre ». */
export const dayTitle = (date: string) =>
  new Intl.DateTimeFormat('fr-FR', { weekday: 'long', day: 'numeric', month: 'long', timeZone: 'UTC' }).format(new Date(`${date}T00:00:00Z`));

export const problemText = (t: TFunction, code: string) => t(`childcare_statement.problem.${code.replaceAll('.', '_')}`, { defaultValue: code });

/** A refused statement write in words: its code's sentence, else the field errors' own, else the server's title. */
export function statementError(t: TFunction, error: unknown): string {
  const problem = toProblem(error);
  const fields = fieldErrorsOf(error);
  if (fields.length > 0) return fields.map((f) => t(`childcare_statement.problem.${f.code.replaceAll('.', '_')}`, { defaultValue: f.message })).join(' ');
  return t(`childcare_statement.error.${(problem.code ?? '').replaceAll('.', '_')}`, { defaultValue: problem.title ?? '' });
}

const STATES = { none: 'childcare_statement.state_none', draft: 'childcare_statement.state_draft', validated: 'childcare_statement.state_validated' } as const;

export const stateLabel = (t: TFunction, state: string) => t(STATES[state as keyof typeof STATES] ?? STATES.none);

const REASONS = {
  family: 'childcare_statement.reason.family', parent_rtt: 'childcare_statement.reason.parent_rtt', child_sick: 'childcare_statement.reason.child_sick',
  carer_absent: 'childcare_statement.reason.carer_absent', holiday: 'childcare_statement.reason.holiday', paid_leave: 'childcare_statement.reason.paid_leave',
} as const;

export const ABSENCE_REASONS = Object.keys(REASONS) as (keyof typeof REASONS)[];

export const reasonLabel = (t: TFunction, reason: string) => t(REASONS[reason as keyof typeof REASONS] ?? REASONS.family);
