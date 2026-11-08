import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { FormDialog } from '@/components/FormDialog';
import { SwitchField } from '@/components/page/SwitchField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { Folded } from '@/components/form/Folded';
import { Reload } from '@/components/form/Reload';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { todayIso } from '@/lib/today';
import { HOLDING, HOLDINGS, invalidateInvestments, type EnvelopeDetail, type Holding, type HoldingBody } from '../api';
import { pastDate } from '@/components/form/pastDate';
import { placeFieldError, presentConflict } from '@/components/form/fieldErrors';
import { HOLDING_KINDS, holdingKindText } from './holdingFormat';
import '@/locales/investments';
import '@/locales/forms';

const ASSET_CLASSES = ['equity', 'bond', 'money_market', 'mixed', 'real_estate', 'capital_guaranteed', 'other'] as const;
const PATHS = {
  name: 'name', kind: 'kind', isin: 'isin', assetClass: 'assetClass', issuer: 'issuer', riskIndicator: 'riskIndicator',
  openedOn: 'openedOn', maturesOn: 'maturesOn', entryCostPct: 'entryCostPct', note: 'note',
} as const;
const FALLBACK = { 'investments.unknown_holding_kind': 'kind', 'investments.invalid_isin': 'isin' } as const;
const OPTIONAL_DATE = /^(\d{4}-\d{2}-\d{2})?$/;

type Values = {
  name: string; kind: (typeof HOLDING_KINDS)[number]; isin: string; assetClass: string; issuer: string; riskIndicator: string;
  openedOn: string; maturesOn: string; entryCostPct: string; note: string; autoPrice: boolean;
};

const valuesOf = (h?: Holding): Values => ({
  name: h?.name ?? '', kind: h ? (HOLDING_KINDS.find((k) => k === h.kind) ?? 'other') : 'fund', isin: h?.isin ?? '', assetClass: h?.assetClass ?? '', issuer: h?.issuer ?? '',
  riskIndicator: h?.riskIndicator != null ? String(h.riskIndicator) : '', openedOn: h?.openedOn ?? '', maturesOn: h?.maturesOn ?? '',
  entryCostPct: h?.entryCostPct != null ? String(h.entryCostPct) : '', note: h?.note ?? '', autoPrice: h?.autoPrice ?? true,
});

/** The PUT replaces every field: the holding as it stands, with the form's values over it. */
const bodyOf = (v: Values, before?: Holding): HoldingBody => ({
  name: v.name.trim(), kind: v.kind, isin: v.isin.trim().toUpperCase() || null, assetClass: v.assetClass || null,
  issuer: v.issuer.trim() || null, riskIndicator: v.riskIndicator ? Number(v.riskIndicator) : null, openedOn: v.openedOn || null,
  maturesOn: v.maturesOn || null, entryCostPct: v.entryCostPct.trim().replace(',', '.') || null, note: v.note.trim() || null,
  closedOn: before?.closedOn ?? null, version: before?.version ?? null, autoPrice: v.autoPrice,
});

/**
 * A new holding, or the correction of one; its rarer fields are folded (screens.md: rare fields folded). « Cours
 * automatique » shows only with an ISIN and the prices on (D9).
 */
export function HoldingDialog({ envelope, editing, pricesOn, onClose, onDone }: {
  envelope: EnvelopeDetail; editing?: Holding; pricesOn: boolean; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const schema = z.object({
    name: z.string().trim().min(1, t('edit.required')).max(100, t('holdings.form.name_too_long')),
    kind: z.enum(HOLDING_KINDS),
    isin: z.string().trim().regex(/^([A-Za-z]{2}[A-Za-z0-9]{9}\d)?$/, t('holdings.form.isin_format')),
    assetClass: z.string(), issuer: z.string().max(100, t('holdings.form.issuer_too_long')), riskIndicator: z.string(),
    openedOn: z.string().regex(OPTIONAL_DATE, t('holdings.form.date_format')),
    maturesOn: z.string().regex(OPTIONAL_DATE, t('holdings.form.date_format')),
    entryCostPct: z.string().trim().regex(/^(\d{1,3}([.,]\d{1,4})?)?$/, t('holdings.form.pct_format')),
    note: z.string().max(2000, t('investments_form.note_too_long')), autoPrice: z.boolean(),
  });
  const form = useForm({ resolver: zodResolver(schema), defaultValues: valuesOf(editing) });
  const errors = useSubmitErrors(form.setError, PATHS);
  const hasIsin = useWatch({ control: form.control, name: 'isin' }).trim() !== '';
  const create = $api.useMutation('post', HOLDINGS, { onSuccess: () => { void invalidateInvestments(queryClient); } });
  const update = $api.useMutation('put', HOLDING, { onSuccess: () => { void invalidateInvestments(queryClient); } });
  const mutation = editing ? update : create;
  const e = form.formState.errors;
  const problem = errors.problem?.kind === 'conflict' ? presentConflict(t, mutation.error) : errors.problem;
  const folded = !!(e.assetClass || e.issuer || e.riskIndicator || e.openedOn || e.maturesOn || e.entryCostPct || e.note);

  return (
    <FormDialog title={editing ? t('holdings.form.edit_title') : t('holdings.form.new_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => {
        const submit = form.handleSubmit((values) => {
          errors.clear();
          const handlers = {
            onSuccess: () => done(editing ? t('holdings.form.saved') : t('holdings.form.created')),
            onError: (error: unknown) => placeFieldError(error, t, form.setError, errors, FALLBACK),
          };
          if (editing) update.mutate({ params: { path: { id: envelope.id, holdingId: editing.id } }, body: bodyOf(values, editing) }, handlers);
          else create.mutate({ params: { path: { id: envelope.id } }, body: bodyOf(values) }, handlers);
        });
        return (
          <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }}>
            {problem ? (
              <>
                <ProblemMessage problem={problem} />
                {problem.kind === 'conflict' ? <Reload onReload={() => invalidateInvestments(queryClient)} /> : null}
              </>
            ) : null}
            <Field label={t('holdings.name')} hint={t('holdings.form.name_hint')} error={e.name?.message}>
              {(c) => <Input {...c} {...form.register('name')} />}
            </Field>
            <div className="grid gap-3 sm:grid-cols-2">
              <Controller control={form.control} name="kind" render={({ field }) => (
                <Field label={t('holdings.kind_label')} error={e.kind?.message}>
                  {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
                    options={HOLDING_KINDS.map((k) => ({ value: k, label: holdingKindText(t, k) }))} />}
                </Field>
              )} />
              <Field label={t('holdings.form.isin')} error={e.isin?.message}>
                {(c) => <Input {...c} autoCapitalize="characters" {...form.register('isin')} />}
              </Field>
            </div>
            {hasIsin && pricesOn ? (
              <Controller control={form.control} name="autoPrice" render={({ field }) => (
                <div className="space-y-1">
                  <SwitchField label={t('holding_prices.auto_price')} checked={field.value} onCheckedChange={field.onChange} />
                  <p className="text-sm text-muted-foreground">{t('holding_prices.auto_price_hint')}</p>
                </div>
              )} />
            ) : null}
            <Folded label={t('holdings.form.more')} hasError={folded}>
              <div className="grid gap-3 sm:grid-cols-2">
                <Controller control={form.control} name="assetClass" render={({ field }) => (
                  <Field label={t('holdings.form.asset_class')} error={e.assetClass?.message}>
                    {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
                      options={[{ value: '', label: t('holdings.form.none') }, ...ASSET_CLASSES.map((k) => ({ value: k, label: t(`holdings.asset_class.${k}`) }))]} />}
                  </Field>
                )} />
                <Field label={t('holdings.form.issuer')} error={e.issuer?.message}>
                  {(c) => <Input {...c} {...form.register('issuer')} />}
                </Field>
                <Controller control={form.control} name="riskIndicator" render={({ field }) => (
                  <Field label={t('holdings.form.risk')} error={e.riskIndicator?.message}>
                    {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
                      options={[{ value: '', label: t('holdings.form.none') }, ...['1', '2', '3', '4', '5', '6', '7'].map((k) => ({ value: k, label: k }))]} />}
                  </Field>
                )} />
                <Field label={t('holdings.form.entry_cost')} error={e.entryCostPct?.message}>
                  {(c) => <Input inputMode="decimal" {...c} {...form.register('entryCostPct')} />}
                </Field>
                <Controller control={form.control} name="openedOn" render={({ field }) => (
                  <Field label={t('holdings.form.opened_on')} error={e.openedOn?.message}>
                    {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
                  </Field>
                )} />
                <Controller control={form.control} name="maturesOn" render={({ field }) => (
                  <Field label={t('holdings.form.matures_on')} error={e.maturesOn?.message}>
                    {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
                  </Field>
                )} />
              </div>
              <Field label={t('holdings.form.note')} hint={t('holdings.form.note_hint')} error={e.note?.message}>
                {(c) => <Textarea {...c} {...form.register('note')} />}
              </Field>
            </Folded>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="outline" onClick={() => done()}>{t('envelope.cancel')}</Button>
              <Button type="submit" disabled={mutation.isPending}>{editing ? t('envelope.save') : t('holdings.form.create')}</Button>
            </div>
          </form>
        );
      }}
    </FormDialog>
  );
}

/** Closing a holding is a PUT with `closedOn`: it stays listed, with its history, and takes no more lines or movements. */
export function CloseHoldingDialog({ envelope, holding, onClose, onDone }: {
  envelope: EnvelopeDetail; holding: Holding; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const form = useForm({ resolver: zodResolver(z.object({ closedOn: pastDate(t) })), defaultValues: { closedOn: todayIso() } });
  const errors = useSubmitErrors(form.setError, { closedOn: 'closedOn' });
  const update = $api.useMutation('put', HOLDING, { onSuccess: () => { void invalidateInvestments(queryClient); } });
  const problem = errors.problem?.kind === 'conflict' ? presentConflict(t, update.error) : errors.problem;

  return (
    <FormDialog title={t('holdings.close.title', { name: holding.name })} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => {
        const submit = form.handleSubmit(({ closedOn }) => {
          errors.clear();
          update.mutate(
            { params: { path: { id: envelope.id, holdingId: holding.id } }, body: { ...bodyOf(valuesOf(holding), holding), closedOn } },
            { onSuccess: () => done(t('holdings.close.done')), onError: errors.onError },
          );
        });
        return (
          <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }}>
            {problem ? (
              <>
                <ProblemMessage problem={problem} />
                {problem.kind === 'conflict' ? <Reload onReload={() => invalidateInvestments(queryClient)} /> : null}
              </>
            ) : null}
            <p className="text-sm text-muted-foreground">{t('holdings.close.body')}</p>
            <Controller control={form.control} name="closedOn" render={({ field }) => (
              <Field label={t('holdings.close.date')} error={form.formState.errors.closedOn?.message}>
                {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
              </Field>
            )} />
            <div className="flex justify-end gap-2">
              <Button type="button" variant="outline" onClick={() => done()}>{t('envelope.cancel')}</Button>
              <Button type="submit" disabled={update.isPending}>{t('holdings.close.submit')}</Button>
            </div>
          </form>
        );
      }}
    </FormDialog>
  );
}
