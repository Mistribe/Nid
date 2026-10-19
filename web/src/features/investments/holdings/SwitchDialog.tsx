import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Controller, useForm } from 'react-hook-form';
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

const PATHS = {
  occurredOn: 'occurredOn', label: 'label', 'from.holdingId': 'from', 'to.holdingId': 'to', 'from.amount': 'amount', 'to.amount': 'amount',
  'from.units': 'fromUnits', 'from.unitPrice': 'fromPrice', 'to.units': 'toUnits', 'to.unitPrice': 'toPrice',
} as const;
const optional = (pattern: RegExp) => (v: string) => v.trim() === '' || pattern.test(v.trim());

/**
 * Issue #34 §7: one switch, two holdings and one amount, written as its two legs by the server (`/switches`). « Échéance »
 * makes the leaving leg a maturity (a structured product's capital moved to another holding).
 */
export function SwitchDialog({ envelope, holdings, onClose, onDone }: {
  envelope: EnvelopeDetail; holdings: Holding[]; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const units = z.string().refine(optional(UNITS), t('holdings.form.units_format'));
  const schema = z.object({
    from: z.string().min(1, t('edit.required')), to: z.string().min(1, t('edit.required')), occurredOn: pastDate(t),
    amount: z.string().regex(POSITIVE_AMOUNT, t('investments_form.positive_amount_format')), maturity: z.boolean(),
    fromUnits: units, fromPrice: units, toUnits: units, toPrice: units, label: z.string().max(200, t('holdings.form.label_too_long')),
  }).refine((v) => v.from !== v.to, { path: ['to'], message: t('holdings.switch.same') });
  const form = useForm({
    resolver: zodResolver(schema),
    defaultValues: {
      from: holdings[0]?.id ?? '', to: holdings[1]?.id ?? '', occurredOn: todayIso(), amount: '', maturity: false,
      fromUnits: '', fromPrice: '', toUnits: '', toPrice: '', label: '',
    },
  });
  const errors = useSubmitErrors(form.setError, PATHS);
  const save = $api.useMutation('post', SWITCHES, { onSuccess: () => { void invalidateInvestments(queryClient); } });
  const e = form.formState.errors;
  const problem = errors.problem?.kind === 'conflict' ? presentConflict(t, save.error) : errors.problem;
  const options = holdings.map((h) => ({ value: h.id, label: h.name }));
  const kindOf = (id: string) => holdings.find((h) => h.id === id)?.kind ?? 'other';
  const [from, to, maturity] = form.watch(['from', 'to', 'maturity']);

  return (
    <FormDialog title={t('holdings.switch.title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => {
        const submit = form.handleSubmit((v) => {
          errors.clear();
          const amount = { amount: toWireAmount(v.amount), currency: envelope.currency };
          const text = (s: string) => (s.trim() === '' ? null : toWireAmount(s));
          const fromUnits = text(v.fromUnits);
          save.mutate({
            params: { path: { id: envelope.id } },
            body: {
              occurredOn: v.occurredOn, maturity: v.maturity, label: v.label.trim() || null,
              from: { holdingId: v.from, amount, units: fromUnits && signedUnits(v.maturity ? 'maturity' : 'switch_out', fromUnits), unitPrice: text(v.fromPrice) },
              to: { holdingId: v.to, amount, units: text(v.toUnits), unitPrice: text(v.toPrice) },
            },
          }, {
            onSuccess: () => done(t('holdings.switch.saved')),
            onError: (error) => placeFieldError(error, t, form.setError, errors, { 'investments.holding_closed': 'occurredOn' }),
          });
        });
        return (
          <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }}>
            {problem ? (
              <>
                <ProblemMessage problem={problem} />
                {problem.kind === 'conflict' ? <Reload onReload={() => invalidateInvestments(queryClient)} /> : null}
              </>
            ) : null}
            <div className="grid gap-3 sm:grid-cols-2">
              <Controller control={form.control} name="from" render={({ field }) => (
                <Field label={t('holdings.switch.from')} error={e.from?.message}>
                  {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange} options={options} />}
                </Field>
              )} />
              <Controller control={form.control} name="to" render={({ field }) => (
                <Field label={t('holdings.switch.to')} error={e.to?.message}>
                  {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange} options={options} />}
                </Field>
              )} />
              <Controller control={form.control} name="occurredOn" render={({ field }) => (
                <Field label={t('envelope.date')} error={e.occurredOn?.message}>
                  {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
                </Field>
              )} />
              <Field label={t('envelope.amount')} error={e.amount?.message}>
                {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
              </Field>
              {hasUnits(kindOf(from)) ? (
                <>
                  <Field label={t('holdings.switch.from_units')} error={e.fromUnits?.message}>
                    {(c) => <Input inputMode="decimal" {...c} {...form.register('fromUnits')} />}
                  </Field>
                  <Field label={t('holdings.switch.from_price')} error={e.fromPrice?.message}>
                    {(c) => <Input inputMode="decimal" {...c} {...form.register('fromPrice')} />}
                  </Field>
                </>
              ) : null}
              {hasUnits(kindOf(to)) ? (
                <>
                  <Field label={t('holdings.switch.to_units')} error={e.toUnits?.message}>
                    {(c) => <Input inputMode="decimal" {...c} {...form.register('toUnits')} />}
                  </Field>
                  <Field label={t('holdings.switch.to_price')} error={e.toPrice?.message}>
                    {(c) => <Input inputMode="decimal" {...c} {...form.register('toPrice')} />}
                  </Field>
                </>
              ) : null}
            </div>
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
