import type { FieldValues, UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { SIGNED_AMOUNT, trimAmount } from '../investments/entries/entries';
import { flowKindText } from '../investments/format';
import { ISO_DATE } from '../subscriptions/edits/amount';
import { Controller } from 'react-hook-form';
import { Field } from '@/components/form/Field';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { DateInput, ValueComparison, errorOf, sameAmount, wireAmount, type KindUi, type Money } from './kindParts';

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
        <Table>
          <caption className="text-left font-semibold">{t('proposal.movements.title')}</caption>
          <TableHeader>
            <TableRow>
              <TableHead scope="col">{t('proposal.movements.on')}</TableHead>
              <TableHead scope="col">{t('proposal.movements.kind')}</TableHead>
              <TableHead scope="col">{t('proposal.movements.label')}</TableHead>
              <TableHead scope="col" className="text-right">{t('proposal.movements.amount')}</TableHead>
              <TableHead scope="col">{t('proposal.movements.state')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {p.movements.map((m) => (
              <TableRow key={m.index}>
                <TableCell>{formatLocalDate(m.on)}</TableCell>
                <TableCell>{flowKindText(t, m)}</TableCell>
                <TableCell>{m.label ?? ''}</TableCell>
                <TableCell className="text-right">{formatMoney(m.amount)}</TableCell>
                <TableCell>{m.recorded ? t('proposal.movements.recorded') : ''}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
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
          <DateInput form={form} name="asOf" label={t('proposal.edit.as_of')} />
          <Field label={t('proposal.edit.amount', { currency: o.value.currency })} error={errorOf(form, 'amount')}>{(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}</Field>
        </>
      ) : null}
      {o.movements.length > 0 ? (
        <fieldset className="space-y-1">
          <legend className="font-semibold">{t('proposal.movements.keep_legend')}</legend>
          {o.movements.map((m, i) => {
            const line = shown[i];
            return (
              <Controller key={i} control={form.control} name={`keep.${i}`} render={({ field }) => (
                <Field inline hint={line?.recorded ? t('proposal.movements.recorded') : undefined}
                  label={t('proposal.movements.keep', {
                    n: i + 1, date: formatLocalDate(m.on), kind: flowKindText(t, m), amount: line ? formatMoney(line.amount) : m.amount,
                  })}>
                  {(c) => <Checkbox {...c} checked={field.value === true} onCheckedChange={(v) => field.onChange(v === true)} />}
                </Field>
              )} />
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
