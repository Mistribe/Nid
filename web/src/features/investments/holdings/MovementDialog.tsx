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
import { Input } from '@/components/ui/input';
import { toWireAmount, POSITIVE_AMOUNT } from '@/lib/amount';
import { Reload } from '@/components/form/Reload';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { todayIso } from '@/lib/today';
import { invalidateInvestments, MOVEMENTS, type EnvelopeDetail, type Holding } from '@/api/investments';
import { pastDate } from '@/components/form/pastDate';
import { placeFieldError, presentConflict } from '@/components/form/fieldErrors';
import { hasUnits, MOVEMENT_KINDS, movementKindText, signedUnits, UNITS } from './holdingFormat';
import '@/locales/investments';

const PATHS = { occurredOn: 'occurredOn', kind: 'kind', amount: 'amount', units: 'units', unitPrice: 'unitPrice', label: 'label' } as const;
const FALLBACK = { 'investments.holding_closed': 'occurredOn' } as const;
const optional = (pattern: RegExp) => (v: string) => v.trim() === '' || pattern.test(v.trim());

/** Spec §2.3: one movement on one holding. Units are typed unsigned; the kind gives their sign on the wire. */
export function MovementDialog({ envelope, holding, onClose, onDone }: {
  envelope: EnvelopeDetail; holding: Holding; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const withUnits = hasUnits(holding.kind);
  const schema = z.object({
    kind: z.enum(MOVEMENT_KINDS),
    occurredOn: pastDate(t),
    amount: z.string().regex(POSITIVE_AMOUNT, t('investments_form.positive_amount_format')),
    units: z.string().refine(optional(UNITS), t('holdings.form.units_format')),
    unitPrice: z.string().refine(optional(UNITS), t('holdings.form.units_format')),
    label: z.string().max(200, t('holdings.form.label_too_long')),
  });
  const form = useForm({
    resolver: zodResolver(schema),
    defaultValues: { kind: 'allocation_in' as (typeof MOVEMENT_KINDS)[number], occurredOn: todayIso(), amount: '', units: '', unitPrice: '', label: '' },
  });
  const errors = useSubmitErrors(form.setError, PATHS);
  const save = $api.useMutation('post', MOVEMENTS, { onSuccess: () => { void invalidateInvestments(queryClient); } });
  const e = form.formState.errors;
  const problem = errors.problem?.kind === 'conflict' ? presentConflict(t, save.error) : errors.problem;

  return (
    <FormDialog title={t('holdings.movement.title', { name: holding.name })} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => {
        const submit = form.handleSubmit((v) => {
          errors.clear();
          save.mutate({
            params: { path: { id: envelope.id, holdingId: holding.id } },
            body: {
              kind: v.kind, occurredOn: v.occurredOn, amount: { amount: toWireAmount(v.amount), currency: envelope.currency },
              units: v.units.trim() ? signedUnits(v.kind, toWireAmount(v.units)) : null,
              unitPrice: v.unitPrice.trim() ? toWireAmount(v.unitPrice) : null, label: v.label.trim() || null,
            },
          }, {
            onSuccess: () => done(t('holdings.movement.saved')),
            onError: (error) => placeFieldError(error, t, form.setError, errors, FALLBACK),
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
              <Controller control={form.control} name="kind" render={({ field }) => (
                <Field label={t('holdings.movement.kind')} error={e.kind?.message}>
                  {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
                    options={MOVEMENT_KINDS.map((k) => ({ value: k, label: movementKindText(t, k) }))} />}
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
              {withUnits ? (
                <>
                  <Field label={t('holdings.units')} hint={t('holdings.movement.units_hint')} error={e.units?.message}>
                    {(c) => <Input inputMode="decimal" {...c} {...form.register('units')} />}
                  </Field>
                  <Field label={t('holdings.unit_price')} error={e.unitPrice?.message}>
                    {(c) => <Input inputMode="decimal" {...c} {...form.register('unitPrice')} />}
                  </Field>
                </>
              ) : null}
            </div>
            <Field label={t('holdings.movement.label')} error={e.label?.message}>
              {(c) => <Input {...c} {...form.register('label')} />}
            </Field>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="outline" onClick={() => done()}>{t('envelope.cancel')}</Button>
              <Button type="submit" disabled={save.isPending}>{t('holdings.movement.submit')}</Button>
            </div>
          </form>
        );
      }}
    </FormDialog>
  );
}
