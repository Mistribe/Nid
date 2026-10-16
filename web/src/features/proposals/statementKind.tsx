import type { FieldValues, UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { SIGNED_AMOUNT, trimAmount } from '../investments/entries/entries';
import { flowKindText } from '../investments/format';
import { ISO_DATE } from '../subscriptions/edits/amount';
import { Field } from '../subscriptions/edits/Field';
import { ValueComparison, errorOf, input, sameAmount, wireAmount, type KindUi, type Money } from './kindParts';

type Movement = { on: string; kind: string; amount: string; label?: string | null; ref?: string | null };
type StatementPayload = { asOf: string; value?: Money | null; movements: Movement[]; note?: string | null; excluded?: number[] | null };
type MovementPreview = { index: number; on: string; kind: string; amount: Money; label?: string | null; recorded: boolean };
type StatementPreview = { value?: unknown; movements: MovementPreview[] };
type StatementForm = { asOf: string; amount: string; keep: boolean[] };

const asStatement = (payload: unknown) => payload as StatementPayload;
const movementsOf = (preview: unknown) => (preview as StatementPreview | null)?.movements ?? [];

/** Checked unless an earlier edit excluded it or, without one, Nid already holds it (spec 4D1 §2). */
const defaultKeep = (o: StatementPayload, preview: unknown) =>
  o.movements.map((_, i) => (o.excluded ? !o.excluded.includes(i) : !movementsOf(preview)[i]?.recorded));

function StatementPreviewView({ preview }: { preview: unknown }) {
  const { t } = useTranslation();
  const p = preview as StatementPreview;
  return (
    <div className="space-y-3">
      {p.value ? <ValueComparison preview={p.value} /> : null}
      {p.movements.length > 0 ? (
        <table className="w-full">
          <caption className="text-left font-semibold">{t('proposal.movements.title')}</caption>
          <thead>
            <tr className="border-b text-left text-sm">
              <th scope="col" className="py-2 pr-4 font-normal">{t('proposal.movements.on')}</th>
              <th scope="col" className="py-2 pr-4 font-normal">{t('proposal.movements.kind')}</th>
              <th scope="col" className="py-2 pr-4 font-normal">{t('proposal.movements.label')}</th>
              <th scope="col" className="py-2 pr-4 text-right font-normal">{t('proposal.movements.amount')}</th>
              <th scope="col" className="py-2 font-normal">{t('proposal.movements.state')}</th>
            </tr>
          </thead>
          <tbody>
            {p.movements.map((m) => (
              <tr key={m.index} className="border-b">
                <td className="py-2 pr-4">{formatLocalDate(m.on)}</td>
                <td className="py-2 pr-4">{flowKindText(t, m)}</td>
                <td className="py-2 pr-4">{m.label ?? ''}</td>
                <td className="py-2 pr-4 text-right">{formatMoney(m.amount)}</td>
                <td className="py-2">{m.recorded ? t('proposal.movements.recorded') : ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}
    </div>
  );
}

function StatementFields({ form, original, preview }: { form: UseFormReturn<FieldValues>; original: unknown; preview: unknown }) {
  const { t } = useTranslation();
  const o = asStatement(original);
  const shown = movementsOf(preview);
  return (
    <>
      {o.value ? (
        <>
          <Field label={t('proposal.edit.as_of')} error={errorOf(form, 'asOf')}>{(c) => <input type="date" {...c} {...form.register('asOf')} className={input} />}</Field>
          <Field label={t('proposal.edit.amount', { currency: o.value.currency })} error={errorOf(form, 'amount')}>{(c) => <input inputMode="decimal" {...c} {...form.register('amount')} className={input} />}</Field>
        </>
      ) : null}
      {o.movements.length > 0 ? (
        <fieldset className="space-y-1">
          <legend className="font-semibold">{t('proposal.movements.keep_legend')}</legend>
          {o.movements.map((m, i) => {
            const line = shown[i];
            return (
              <Field key={i} inline hint={line?.recorded ? t('proposal.movements.recorded') : undefined}
                label={t('proposal.movements.keep', {
                  n: i + 1, date: formatLocalDate(m.on), kind: flowKindText(t, m), amount: line ? formatMoney(line.amount) : m.amount,
                })}>
                {(c) => <input type="checkbox" {...c} {...form.register(`keep.${i}`)} />}
              </Field>
            );
          })}
        </fieldset>
      ) : null}
    </>
  );
}

export const statement: KindUi = {
  label: 'proposals.kind.statement',
  summary: (t, p) => {
    const o = asStatement(p.editedPayload ?? p.payload);
    return t('proposals.summary.statement', {
      name: p.targetName ?? t('proposals.unknown_target'), date: formatLocalDate(o.asOf, 'short'), count: o.movements.length,
    });
  },
  Preview: StatementPreviewView,
  schema: (t, original) => {
    const hasValue = !!asStatement(original).value;
    return z.object({
      asOf: hasValue ? z.string().regex(ISO_DATE, t('edit.required')) : z.string(),
      amount: hasValue ? z.string().regex(SIGNED_AMOUNT, t('investments_form.amount_format')) : z.string(),
      keep: z.array(z.boolean()),
    });
  },
  toForm: (original, preview) => {
    const o = asStatement(original);
    return { asOf: o.asOf, amount: o.value ? trimAmount(o.value.amount) : '', keep: defaultKeep(o, preview) };
  },
  toPayload: (values, original, preview) => {
    const v = values as StatementForm;
    const o = asStatement(original);
    const before = defaultKeep(o, preview);
    const sameValue = !o.value || (v.asOf === o.asOf && sameAmount(v.amount, o.value.amount));
    if (sameValue && v.keep.every((keep, i) => keep === before[i])) return null;
    return {
      ...o,
      // The value as it was unless really changed: « 23500 » typed for « 23500.00 » sends the original.
      ...(o.value && !sameValue ? { asOf: v.asOf, value: { amount: wireAmount(v.amount), currency: o.value.currency } } : {}),
      excluded: v.keep.flatMap((keep, i) => (keep ? [] : [i])),
    };
  },
  field: (path) => (path === 'payload.asOf' ? 'asOf' : path.startsWith('payload.value') ? 'amount' : undefined),
  Fields: StatementFields,
  nothing: (values, original) => !asStatement(original).value && (values as StatementForm).keep.every((keep) => !keep),
  link: (p) => ({ to: '/investments/$id', params: { id: p.targetItemId } }),
};
