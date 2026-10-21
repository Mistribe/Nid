import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { X } from 'lucide-react';
import { Controller, useFieldArray, useForm, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { FormDialog } from '@/components/FormDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { toWireAmount } from '../../subscriptions/edits/amount';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { invalidateInvestments, SWITCHES, type EnvelopeDetail, type Holding } from '../api';
import { pastDate, placeFieldError, POSITIVE_AMOUNT, presentConflict } from '../entries/entries';
import { hasUnits, signedUnits, UNITS } from './holdingFormat';
import '@/locales/investments';

type Side = 'from' | 'to';
const SIDES = ['from', 'to'] as const;
const LEG_FIELDS = { holdingId: 'holdingId', amount: 'amount', units: 'units', unitPrice: 'price' } as const;
const MAX_LEGS = 10;
/** The server's paths (`from[0].amount`) onto the form's (`from.0.amount`). */
function formPath(path: string) {
  if (path === 'occurredOn' || path === 'label') return path;
  const leg = /^(from|to)\[(\d+)\]\.(holdingId|amount|units|unitPrice)$/.exec(path);
  return leg ? (`${leg[1] as Side}.${Number(leg[2])}.${LEG_FIELDS[leg[3] as keyof typeof LEG_FIELDS]}` as const) : undefined;
}
const optional = (pattern: RegExp) => (v: string) => v.trim() === '' || pattern.test(v.trim());
const emptyLeg = (holdingId = '') => ({ holdingId, amount: '', units: '', price: '' });

/**
 * Issue #34 §7 and #45: one switch, one or more holdings on each side, written by the server as one operation (`/switches`). With
 * one holding on each side a single amount stands for both legs (copied, never computed); with more, each leg has its amount as
 * printed. « Échéance » makes the leaving legs maturities (a structured product's capital moved to other holdings).
 */
export function SwitchDialog({ envelope, holdings, onClose, onDone }: {
  envelope: EnvelopeDetail; holdings: Holding[]; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const units = z.string().refine(optional(UNITS), t('holdings.form.units_format'));
  const amount = z.string().refine(optional(POSITIVE_AMOUNT), t('investments_form.positive_amount_format'));
  const leg = z.object({ holdingId: z.string().min(1, t('edit.required')), amount, units, price: units });
  const schema = z.object({
    from: z.array(leg).min(1), to: z.array(leg).min(1), occurredOn: pastDate(t), maturity: z.boolean(),
    label: z.string().max(200, t('holdings.form.label_too_long')),
  }).superRefine((v, ctx) => {
    const seen = new Set<string>();
    for (const side of SIDES)
      v[side].forEach((l, i) => {
        if (l.holdingId !== '' && seen.has(l.holdingId)) ctx.addIssue({ code: 'custom', path: [side, i, 'holdingId'], message: t('holdings.switch.same') });
        seen.add(l.holdingId);
        // One holding on each side: the first leg's amount stands for both.
        const needed = v.from.length + v.to.length > 2 ? true : side === 'from';
        if (needed && l.amount.trim() === '') ctx.addIssue({ code: 'custom', path: [side, i, 'amount'], message: t('investments_form.positive_amount_format') });
      });
  });
  const form = useForm({
    resolver: zodResolver(schema),
    defaultValues: {
      from: [emptyLeg(holdings[0]?.id)], to: [emptyLeg(holdings[1]?.id)], occurredOn: todayIso(), maturity: false, label: '',
    },
  });
  const errors = useSubmitErrors(form.setError, formPath);
  const save = $api.useMutation('post', SWITCHES, { onSuccess: () => { void invalidateInvestments(queryClient); } });
  const e = form.formState.errors;
  const problem = errors.problem?.kind === 'conflict' ? presentConflict(t, save.error) : errors.problem;
  const from = useFieldArray({ control: form.control, name: 'from' });
  const to = useFieldArray({ control: form.control, name: 'to' });
  const maturity = form.watch('maturity');
  const simple = from.fields.length === 1 && to.fields.length === 1;

  return (
    <FormDialog title={t('holdings.switch.title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => {
        const submit = form.handleSubmit((v) => {
          errors.clear();
          const text = (s: string) => (s.trim() === '' ? null : toWireAmount(s));
          const money = (s: string) => ({ amount: toWireAmount(s), currency: envelope.currency });
          const legs = (side: Side) => v[side].map((l) => {
            const legUnits = text(l.units);
            return {
              holdingId: l.holdingId,
              amount: money(simple ? v.from[0]!.amount : l.amount),
              units: legUnits && side === 'from' ? signedUnits(v.maturity ? 'maturity' : 'switch_out', legUnits) : legUnits,
              unitPrice: text(l.price),
            };
          });
          save.mutate({
            params: { path: { id: envelope.id } },
            body: { occurredOn: v.occurredOn, maturity: v.maturity, label: v.label.trim() || null, from: legs('from'), to: legs('to') },
          }, {
            onSuccess: () => done(t('holdings.switch.saved')),
            onError: (error) => placeFieldError(error, t, form.setError, errors, { 'investments.holding_closed': 'occurredOn' }),
          });
        });
        return (
          <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }}>
            {problem ? (
              <>
                <ProblemMessage problem={problem} />
                {problem.kind === 'conflict' ? <Reload onReload={() => invalidateInvestments(queryClient)} /> : null}
              </>
            ) : null}
            <div className="grid gap-3 sm:grid-cols-2">
              <Controller control={form.control} name="occurredOn" render={({ field }) => (
                <Field label={t('envelope.date')} error={e.occurredOn?.message}>
                  {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
                </Field>
              )} />
              {simple ? (
                <Field label={t('envelope.amount')} error={e.from?.[0]?.amount?.message}>
                  {(c) => <Input inputMode="decimal" {...c} {...form.register('from.0.amount')} />}
                </Field>
              ) : null}
            </div>
            {SIDES.map((side) => {
              const array = side === 'from' ? from : to;
              const many = array.fields.length > 1;
              return (
                <fieldset key={side} className="space-y-3">
                  <legend className="text-sm font-medium">{t(`holdings.switch.${side}`)}</legend>
                  {array.fields.map((row, i) => (
                    <Leg key={row.id} form={form} side={side} index={i} holdings={holdings} simple={simple}
                      suffix={many ? ` ${i + 1}` : ''} onRemove={many ? () => array.remove(i) : undefined} />
                  ))}
                  {array.fields.length < MAX_LEGS ? (
                    <Button type="button" variant="outline" size="sm" onClick={() => array.append(emptyLeg())}>
                      {t(`holdings.switch.add_${side}`)}
                    </Button>
                  ) : null}
                </fieldset>
              );
            })}
            <Field label={t('holdings.movement.label')} error={e.label?.message}>
              {(c) => <Input {...c} {...form.register('label')} />}
            </Field>
            <Controller control={form.control} name="maturity" render={({ field }) => (
              <Field inline label={t('holdings.switch.maturity')} hint={maturity ? t('holdings.switch.maturity_hint') : undefined}>
                {(c) => <Checkbox {...c} checked={field.value} onCheckedChange={(v) => field.onChange(v === true)} />}
              </Field>
            )} />
            <div className="flex justify-end gap-2">
              <Button type="button" variant="outline" onClick={() => done()}>{t('envelope.cancel')}</Button>
              <Button type="submit" disabled={save.isPending}>{t('holdings.switch.submit')}</Button>
            </div>
          </form>
        );
      }}
    </FormDialog>
  );
}

type SwitchForm = UseFormReturn<{
  from: { holdingId: string; amount: string; units: string; price: string }[];
  to: { holdingId: string; amount: string; units: string; price: string }[];
  occurredOn: string; maturity: boolean; label: string;
}>;

/** One leg: its holding, its amount unless one amount stands for both legs, and its units and price for a holding that has units. */
function Leg({ form, side, index, holdings, simple, suffix, onRemove }: {
  form: SwitchForm; side: Side; index: number; holdings: Holding[]; simple: boolean; suffix: string; onRemove?: () => void;
}) {
  const { t } = useTranslation();
  const e = form.formState.errors[side]?.[index];
  const holdingId = form.watch(`${side}.${index}.holdingId`);
  const kind = holdings.find((h) => h.id === holdingId)?.kind ?? 'other';
  const options = holdings.map((h) => ({ value: h.id, label: h.name }));
  return (
    <div className="grid items-start gap-3 sm:grid-cols-[1fr_1fr_auto]">
      <Controller control={form.control} name={`${side}.${index}.holdingId`} render={({ field }) => (
        <Field label={t(`holdings.switch.${side}`) + suffix} error={e?.holdingId?.message}>
          {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange} options={options} />}
        </Field>
      )} />
      {simple ? <span aria-hidden="true" /> : (
        <Field label={t(`holdings.switch.${side}_amount`) + suffix} error={e?.amount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register(`${side}.${index}.amount`)} />}
        </Field>
      )}
      {onRemove ? (
        <Button type="button" variant="ghost" size="icon" className="mt-6" aria-label={t('holdings.switch.remove') + suffix} onClick={onRemove}>
          <X aria-hidden="true" />
        </Button>
      ) : <span aria-hidden="true" />}
      {hasUnits(kind) ? (
        <>
          <Field label={t(`holdings.switch.${side}_units`) + suffix} error={e?.units?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register(`${side}.${index}.units`)} />}
          </Field>
          <Field label={t(`holdings.switch.${side}_price`) + suffix} error={e?.price?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register(`${side}.${index}.price`)} />}
          </Field>
        </>
      ) : null}
    </div>
  );
}
