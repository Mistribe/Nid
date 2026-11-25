import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef } from 'react';
import { Controller, FormProvider, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ASSET, invalidateAssets, type AssetView } from '@/api/assets';
import { toProblem } from '@/api/errors';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { Folded } from '@/components/form/Folded';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { FormDialog } from '@/components/FormDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { todayIso } from '@/lib/today';
import { AssetFields, DepreciationField, NoteField, VehicleFields } from './AssetFields';
import { editAssetPath, editAssetSchema, editDefaults, refusalText, toEditBody, type EditAssetValues } from './assetForm';
import '@/locales/assets';
import '@/locales/loans';

/** The asset's fields, the purchase included; its values are the valuations tab's, its owners the sharing menu's. */
export function EditAssetDialog({ asset, onClose, onDone }: { asset: AssetView; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  return (
    <FormDialog title={t('assets_form.edit_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => <EditForm asset={asset} titleId={titleId} done={done} />}
    </FormDialog>
  );
}

function EditForm({ asset, titleId, done }: { asset: AssetView; titleId: string; done: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<EditAssetValues>({ resolver: zodResolver(editAssetSchema(t)), defaultValues: editDefaults(asset) });
  const errors = useSubmitErrors(form.setError, editAssetPath, (field) => refusalText(t, field.code));
  const edit = $api.useMutation('put', ASSET);
  // A conflict refetches the asset: the form takes the fresh values with the fresh version, so a retry never overwrites them blindly.
  const seen = useRef(asset.version);
  useEffect(() => {
    if (asset.version === seen.current) return;
    seen.current = asset.version;
    form.reset(editDefaults(asset));
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the version is the event.
  }, [asset.version]);
  const e = form.formState.errors;

  return (
    <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
      event.stopPropagation();
      void form.handleSubmit((values) => {
        errors.clear();
        edit.mutate({ params: { path: { id: asset.id } }, body: toEditBody(values, seen.current) }, {
          onSuccess: () => { void invalidateAssets(queryClient); done(t('assets_form.saved')); },
          onError: (error) => { if (toProblem(error).status === 409) void invalidateAssets(queryClient); errors.onError(error); },
        });
      })(event);
    }}>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <FormProvider {...form}>
        <AssetFields />
        <VehicleFields />
        {asset.kind === 'vehicle' ? <Folded label={t('assets_form.customize')} hasError={Boolean(e.depreciationRate)}><DepreciationField /></Folded> : null}
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={t('assets_form.purchase_price')} error={e.purchasePrice?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register('purchasePrice')} />}
          </Field>
          <Controller control={form.control} name="purchasedOn" render={({ field }) => (
            <Field label={t('assets_form.purchased_on')} error={e.purchasedOn?.message}>
              {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
            </Field>
          )} />
        </div>
        <NoteField />
      </FormProvider>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
        <Button type="submit" disabled={edit.isPending}>{t('assets_form.save')}</Button>
      </div>
    </form>
  );
}
