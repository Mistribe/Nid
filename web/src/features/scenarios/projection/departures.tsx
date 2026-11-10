import { useFormContext } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { PERSONS } from '@/api/persons';
import { Field } from '@/components/form/Field';
import { Input } from '@/components/ui/input';
import { toWireAmount } from '@/lib/amount';
import type { ProjectionDocument } from './api';
import '@/locales/scenarios';

/** A departure as typed, per adult (spec §2 « Scénarios »): an empty field keeps the household's. */
export type DepartureDraft = { years: string; months: string; pension: string };
export type DeparturesDraft = Record<string, DepartureDraft>;
export type Adult = { id: string; displayName: string; retirementAge: number; retirementMonths: number };
type Retirements = NonNullable<ProjectionDocument['retirements']>;

const INTEGER = /^\d+$/;
const AMOUNT = /^\d+([.,]\d{1,4})?$/;
const bare = (text: string | undefined) => (text ?? '').replace(/\s/g, '');

/** The household's adults (the only persons a departure can move). */
export function useAdults(): Adult[] {
  const { $api } = useApi();
  return ($api.useQuery('get', PERSONS, {}).data ?? []).filter((p) => p.kind === 'adult');
}

/** The draft of saved departures (each pension with a French comma). */
export const departuresDraft = (retirements?: Retirements | null): DeparturesDraft =>
  Object.fromEntries((retirements ?? []).flatMap((r) => (r.person ? [[r.person, {
    years: String(r.years), months: String(r.months), pension: r.monthlyPension == null ? '' : r.monthlyPension.replace('.', ','),
  }]] : [])));

/** Only the adults who typed something; the age they left empty is the household's. */
export function retirementsOf(draft: DeparturesDraft | undefined, adults: Adult[]): Retirements {
  return adults.flatMap((a) => {
    const d = draft?.[a.id];
    if (!d || (!bare(d.years) && !bare(d.months) && !bare(d.pension))) return [];
    const years = bare(d.years) ? Number(bare(d.years)) : a.retirementAge;
    const months = bare(d.months) ? Number(bare(d.months)) : bare(d.years) ? 0 : a.retirementMonths;
    return [{ person: a.id, years, months, ...(bare(d.pension) ? { monthlyPension: toWireAmount(d.pension) } : {}) }];
  });
}

/** Every typed figure reads as a number. */
export const departuresComplete = (draft: DeparturesDraft | undefined) =>
  Object.values(draft ?? {}).every((d) => [d.years, d.months].every((v) => !bare(v) || INTEGER.test(bare(v))) && (!bare(d.pension) || AMOUNT.test(bare(d.pension))));

/** A server path `retirements[i].years` as the draft's field, `i` being the i-th adult sent. */
export function departurePath(path: string, sent: Retirements): `departures.${string}` | undefined {
  const at = /^retirements\[(\d+)\](?:\.(\w+))?$/.exec(path);
  const person = at ? sent[Number(at[1])]?.person : undefined;
  if (!at || !person) return undefined;
  const field = at[2] === 'months' ? 'months' : at[2] === 'monthlyPension' ? 'pension' : 'years';
  return `departures.${person}.${field}`;
}

/** The « Départs » block: per adult an age at departure (years, months) and a monthly pension. */
export function DepartureFields({ adults }: { adults: Adult[] }) {
  const { t } = useTranslation();
  const form = useFormContext<{ departures: DeparturesDraft }>();
  const input = (name: `departures.${string}`, pattern: RegExp) => form.register(name, {
    validate: (v) => { const typed = bare(typeof v === 'string' ? v : ''); return !typed || pattern.test(typed) || t('scenarios.projection.form.invalid_number'); },
  });
  const error = (name: `departures.${string}`) => { const e = form.getFieldState(name, form.formState).error?.message; return typeof e === 'string' ? e : undefined; };
  return (
    <div className="space-y-3">
      <h3 className="font-medium">{t('scenarios.projection.departures.title')}</h3>
      <p className="text-sm text-muted-foreground">{t('scenarios.projection.departures.intro')}</p>
      {adults.map((a) => (
        <fieldset key={a.id} className="grid gap-3 sm:grid-cols-3">
          <legend className="mb-1 text-sm font-medium">{a.displayName}</legend>
          <Field label={t('scenarios.projection.departures.years', { name: a.displayName })} error={error(`departures.${a.id}.years`)}
            hint={t('scenarios.projection.form.household_value', { value: t('scenarios.projection.departures.age', { years: a.retirementAge, months: a.retirementMonths }) })}>
            {(c) => <Input inputMode="numeric" {...c} {...input(`departures.${a.id}.years`, INTEGER)} />}
          </Field>
          <Field label={t('scenarios.projection.departures.months', { name: a.displayName })} error={error(`departures.${a.id}.months`)}>
            {(c) => <Input inputMode="numeric" {...c} {...input(`departures.${a.id}.months`, INTEGER)} />}
          </Field>
          <Field label={t('scenarios.projection.departures.pension', { name: a.displayName })} error={error(`departures.${a.id}.pension`)}
            hint={t('scenarios.projection.departures.pension_hint')}>
            {(c) => <Input inputMode="decimal" {...c} {...input(`departures.${a.id}.pension`, AMOUNT)} />}
          </Field>
        </fieldset>
      ))}
    </div>
  );
}
