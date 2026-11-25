import { Controller, useFormContext } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { ASSET_KINDS } from '@/api/assets';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { InfoTip } from '@/components/InfoTip';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import type { AssetFieldsValues } from './assetForm';
import { kindText } from './format';
import '@/locales/assets';

/** The asset's name, its kind, and for a precious metal its weight and fineness: the creation's and the edit's alike. */
export function AssetFields() {
  const { t } = useTranslation();
  const form = useFormContext<AssetFieldsValues>();
  const e = form.formState.errors;
  const isMetal = form.watch('kind') === 'precious_metal';
  return (
    <>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t('assets_form.name')} error={e.name?.message}>
          {(c) => <Input {...c} {...form.register('name')} />}
        </Field>
        <Controller control={form.control} name="kind" render={({ field }) => (
          <Field label={t('assets_form.kind')} error={e.kind?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange} options={ASSET_KINDS.map((k) => ({ value: k, label: kindText(t, k) }))} />}
          </Field>
        )} />
      </div>
      {isMetal ? (
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={t('assets_form.weight')} error={e.weightGrams?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register('weightGrams')} />}
          </Field>
          <Field label={t('assets_form.fineness')} tip={<InfoTip label={t('assets_form.fineness')}>{t('assets_form.fineness_tip')}</InfoTip>} error={e.fineness?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register('fineness')} />}
          </Field>
        </div>
      ) : null}
    </>
  );
}

/** The free note, in « Plus d’options » on the creation and at the foot of the edit. */
export function NoteField() {
  const { t } = useTranslation();
  const form = useFormContext<AssetFieldsValues>();
  return (
    <Field label={t('assets_form.note')} error={form.formState.errors.note?.message}>
      {(c) => <Textarea {...c} {...form.register('note')} />}
    </Field>
  );
}
