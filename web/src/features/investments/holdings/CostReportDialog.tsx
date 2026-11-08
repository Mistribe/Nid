import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { Field } from '@/components/form/Field';
import { FormDialog } from '@/components/FormDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Folded } from '@/components/form/Folded';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { todayIso } from '@/lib/today';
import { COST_REPORT, invalidateInvestments, type CostReport, type EnvelopeDetail, type Holding } from '@/api/investments';
import { placeFieldError } from '@/components/form/fieldErrors';
import '@/locales/investments';
import '@/locales/forms';

const REQUIRED = ['grossReturnPct', 'totalFeesPct', 'netReturnPct'] as const;
const DETAIL = ['contractFeesPct', 'supportFeesPct', 'retrocessionPct', 'netAnnualized5yPct'] as const;
const PCT = /^-?\d{1,4}([.,]\d{1,4})?$/;
const FIELDS = [...REQUIRED, ...DETAIL, 'year', 'sourceLabel'] as const;
const pathOf = (path: string) => FIELDS.find((k) => k === path);

const text = (n: number | null | undefined) => (n == null ? '' : String(n));
/** A percentage stated on the report, not an amount: the body takes numbers. */
const number = (s: string) => (s.trim() === '' ? null : Number(s.trim().replace(',', '.')));

/** Issue #34 §2.5: the yearly cost report (art. A.522-1) as printed; saving a year that has one replaces it. */
export function CostReportDialog({ envelope, holding, editing, onClose, onDone }: {
  envelope: EnvelopeDetail; holding: Holding; editing?: CostReport; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const thisYear = Number(todayIso().slice(0, 4));
  const pct = z.string().refine((v) => v.trim() === '' || PCT.test(v.trim()), t('holdings.form.pct_format'));
  const schema = z.object({
    year: z.string().refine((v) => /^\d{4}$/.test(v) && Number(v) >= 1900 && Number(v) <= thisYear, t('holdings.costs.year_range')),
    grossReturnPct: pct.refine((v) => v.trim() !== '', t('edit.required')),
    totalFeesPct: pct.refine((v) => v.trim() !== '', t('edit.required')),
    netReturnPct: pct.refine((v) => v.trim() !== '', t('edit.required')),
    contractFeesPct: pct, supportFeesPct: pct, retrocessionPct: pct, netAnnualized5yPct: pct,
    sourceLabel: z.string().max(200, t('holdings.form.label_too_long')),
  });
  const form = useForm({
    resolver: zodResolver(schema),
    defaultValues: {
      year: String(editing?.year ?? thisYear - 1), grossReturnPct: text(editing?.grossReturnPct), totalFeesPct: text(editing?.totalFeesPct),
      netReturnPct: text(editing?.netReturnPct), contractFeesPct: text(editing?.contractFeesPct), supportFeesPct: text(editing?.supportFeesPct),
      retrocessionPct: text(editing?.retrocessionPct), netAnnualized5yPct: text(editing?.netAnnualized5yPct), sourceLabel: editing?.sourceLabel ?? '',
    },
  });
  const errors = useSubmitErrors(form.setError, pathOf);
  const save = $api.useMutation('put', COST_REPORT, { onSuccess: () => { void invalidateInvestments(queryClient); } });
  const e = form.formState.errors;

  return (
    <FormDialog title={t('holdings.costs.dialog_title', { name: holding.name })} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => {
        const submit = form.handleSubmit((v) => {
          errors.clear();
          save.mutate({
            params: { path: { id: envelope.id, holdingId: holding.id, year: Number(v.year) } },
            body: {
              grossReturnPct: number(v.grossReturnPct), totalFeesPct: number(v.totalFeesPct), netReturnPct: number(v.netReturnPct),
              contractFeesPct: number(v.contractFeesPct), supportFeesPct: number(v.supportFeesPct), retrocessionPct: number(v.retrocessionPct),
              netAnnualized5yPct: number(v.netAnnualized5yPct), sourceLabel: v.sourceLabel.trim() || null,
            },
          }, {
            onSuccess: () => done(t('holdings.costs.saved')),
            onError: (error) => placeFieldError(error, t, form.setError, errors, {}),
          });
        });
        return (
          <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }}>
            {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label={t('holdings.costs.year')} error={e.year?.message}>
                {(c) => <Input inputMode="numeric" readOnly={!!editing} {...c} {...form.register('year')} />}
              </Field>
              {REQUIRED.map((k) => (
                <Field key={k} label={t(`holdings.costs.${k}`)} error={e[k]?.message}>
                  {(c) => <Input inputMode="decimal" {...c} {...form.register(k)} />}
                </Field>
              ))}
            </div>
            <Folded label={t('holdings.costs.more')} hasError={DETAIL.some((k) => e[k]) || !!e.sourceLabel}>
              <div className="grid gap-3 sm:grid-cols-2">
                {DETAIL.map((k) => (
                  <Field key={k} label={t(`holdings.costs.${k}`)} error={e[k]?.message}>
                    {(c) => <Input inputMode="decimal" {...c} {...form.register(k)} />}
                  </Field>
                ))}
              </div>
              <Field label={t('holdings.snapshot.source')} error={e.sourceLabel?.message}>
                {(c) => <Input {...c} {...form.register('sourceLabel')} />}
              </Field>
            </Folded>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="outline" onClick={() => done()}>{t('envelope.cancel')}</Button>
              <Button type="submit" disabled={save.isPending}>{t('envelope.save')}</Button>
            </div>
          </form>
        );
      }}
    </FormDialog>
  );
}
