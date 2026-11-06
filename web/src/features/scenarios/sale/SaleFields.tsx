import { Controller, useFormContext, type Path } from 'react-hook-form';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { ComboboxField } from '@/components/form/ComboboxField';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { Input } from '@/components/ui/input';
import { ISO_DATE, toFieldAmount, toWireAmount } from '../../subscriptions/edits/amount';
import type { SaleDocument, SaleScenario } from '../api';
import { usePickOptions } from '../projection/pickOptions';
import '@/locales/forms';
import '@/locales/scenarios';

/** Issue #416: a sale as typed, its name and visibility then its document; every figure a string, the server validates it. */
export type SaleDraft = {
  name: string; visibility: string; property: string; saleOn: string; price: string; agencyFees: string; penalties: string; horizonYears: string;
};

export const newSaleDraft = (): SaleDraft => ({
  name: '', visibility: 'shared', property: '', saleOn: '', price: '', agencyFees: '0', penalties: '0', horizonYears: '10',
});

/** A saved sale back as the draft; a property the reader cannot see comes back empty, to be picked again. */
export const fromSale = (s: Pick<SaleScenario, 'name' | 'visibility' | 'document'>): SaleDraft => ({
  name: s.name, visibility: s.visibility, property: s.document.property ?? '', saleOn: s.document.saleOn, price: toFieldAmount(s.document.price ?? ''),
  agencyFees: toFieldAmount(s.document.agencyFees ?? '0'), penalties: toFieldAmount(s.document.penalties ?? '0'), horizonYears: String(s.document.horizonYears),
});

/** The wire document; an unreadable date is left out (the server names it on its field rather than refusing the whole body). */
export const toSaleDocument = (d: SaleDraft): SaleDocument => ({
  version: 1, property: d.property || null, ...(ISO_DATE.test(d.saleOn) ? { saleOn: d.saleOn } : {}),
  price: toWireAmount(d.price), agencyFees: toWireAmount(d.agencyFees), penalties: toWireAmount(d.penalties),
  horizonYears: Number.parseInt(d.horizonYears, 10) || 0,
}) as SaleDocument;

/** Every server path is a field of the draft. */
export const salePath = (path: string): Path<SaleDraft> | undefined =>
  (['name', 'visibility', 'property', 'saleOn', 'price', 'agencyFees', 'penalties', 'horizonYears'] as const).find((f) => f === path);

function TextField({ name, label, hint, numeric, text }: { name: Path<SaleDraft>; label: string; hint?: string; numeric?: boolean; text?: boolean }) {
  const form = useFormContext<SaleDraft>();
  const message = form.getFieldState(name, form.formState).error?.message;
  return (
    <Field label={label} hint={hint} error={typeof message === 'string' ? message : undefined}>
      {(c) => <Input inputMode={text ? undefined : numeric ? 'numeric' : 'decimal'} {...c} {...form.register(name)} />}
    </Field>
  );
}

/** The sale's fields, read from the surrounding `FormProvider<SaleDraft>`; the properties offered are the reader's in `scope`. */
export function SaleFields({ scope }: { scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const form = useFormContext<SaleDraft>();
  const properties = usePickOptions('properties', scope);
  const visibilityId = useId();
  return (
    <>
      <TextField name="name" label={t('scenarios.form.name')} text />
      <div className="space-y-1">
        <span id={visibilityId} className="block text-sm">{t('scenarios.form.visibility')}</span>
        <Controller control={form.control} name="visibility" render={({ field }) => (
          <Segmented aria-labelledby={visibilityId} value={field.value} onValueChange={field.onChange}
            options={(['shared', 'private'] as const).map((v) => ({ value: v, label: t(`scenarios.form.visibility_${v}`) }))} />
        )} />
      </div>
      <Controller control={form.control} name="property" render={({ field, fieldState }) => (
        <Field label={t('scenarios.sale.property')} error={fieldState.error?.message}>
          {(c) => <ComboboxField {...c} value={field.value} onValueChange={field.onChange} options={properties}
            searchPlaceholder={t('scenarios.sale.property_search')} empty={t('scenarios.sale.property_none')} />}
        </Field>
      )} />
      <div className="grid gap-3 sm:grid-cols-3">
        <Controller control={form.control} name="saleOn" render={({ field, fieldState }) => (
          <Field label={t('scenarios.sale.sale_on')} error={fieldState.error?.message}>
            {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
          </Field>
        )} />
        <TextField name="price" label={t('scenarios.sale.price')} />
        <TextField name="agencyFees" label={t('scenarios.sale.agency_fees')} />
        <TextField name="penalties" label={t('scenarios.sale.penalties')} hint={t('scenarios.sale.penalties_hint')} />
        <TextField name="horizonYears" label={t('scenarios.sale.horizon')} numeric />
      </div>
    </>
  );
}
