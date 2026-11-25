import { Controller, useFormContext } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { ASSET_ENERGIES, ASSET_KINDS } from '@/api/assets';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { InfoTip } from '@/components/InfoTip';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import type { AssetFieldsValues } from './assetForm';
import { todayIso } from '@/lib/today';
import { energyText, kindText } from './format';
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

/** A vehicle's registration, mileage reading, fiscal horsepower and energy: nothing for another kind. */
export function VehicleFields() {
  const { t } = useTranslation();
  const form = useFormContext<AssetFieldsValues>();
  const e = form.formState.errors;
  if (form.watch('kind') !== 'vehicle') return null;
  return (
    <div className="grid gap-3 sm:grid-cols-2">
      <Controller control={form.control} name="registeredOn" render={({ field }) => (
        <Field label={t('assets_form.registered_on')} error={e.registeredOn?.message}>
          {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
        </Field>
      )} />
      <Field label={t('assets_form.fiscal_hp')} tip={<InfoTip label={t('assets_form.fiscal_hp')}>{t('assets_form.fiscal_hp_tip')}</InfoTip>} error={e.fiscalHp?.message}>
        {(c) => <Input inputMode="numeric" {...c} {...form.register('fiscalHp')} />}
      </Field>
      <Field label={t('assets_form.mileage')} error={e.mileageKm?.message}>
        {(c) => <Input inputMode="numeric" {...c} {...form.register('mileageKm')} />}
      </Field>
      <Controller control={form.control} name="mileageOn" render={({ field }) => (
        <Field label={t('assets_form.mileage_on')} error={e.mileageOn?.message}>
          {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
        </Field>
      )} />
      <Controller control={form.control} name="energy" render={({ field }) => (
        <Field label={t('assets_form.energy')} error={e.energy?.message}>
          {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange} options={[{ value: '', label: '—' }, ...ASSET_ENERGIES.map((k) => ({ value: k, label: energyText(t, k) }))]} />}
        </Field>
      )} />
    </div>
  );
}

/** The yearly loss of value of a vehicle, which turns its value into an estimate: nothing for another kind. */
export function DepreciationField() {
  const { t } = useTranslation();
  const form = useFormContext<AssetFieldsValues>();
  if (form.watch('kind') !== 'vehicle') return null;
  return (
    <Field label={t('assets_form.depreciation')} tip={<InfoTip label={t('assets_form.depreciation')}>{t('assets_form.depreciation_tip')}</InfoTip>}
      hint={t('assets_form.depreciation_hint')} error={form.formState.errors.depreciationRate?.message}>
      {(c) => <Input inputMode="decimal" {...c} {...form.register('depreciationRate')} />}
    </Field>
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
