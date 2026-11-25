import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { ASSET, invalidateAssets, type AssetView } from '@/api/assets';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { FormDialog } from '@/components/FormDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { todayIso } from '@/lib/today';
import { checkVehicle, editDefaults, refusalText, toEditBody, type EditAssetValues } from './assetForm';
import '@/locales/assets';
import '@/locales/forms';

/** A new reading of the mileage: the edit replaces every field, so it sends the asset as it stands with the reading changed. */
export function MileageDialog({ asset, onClose, onDone }: { asset: AssetView; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  return (
    <FormDialog title={t('assets_form.update_mileage')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => <MileageForm asset={asset} titleId={titleId} done={done} />}
    </FormDialog>
  );
}

function MileageForm({ asset, titleId, done }: { asset: AssetView; titleId: string; done: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const schema = z.object({ mileageKm: z.string(), mileageOn: z.string() }).superRefine((v, ctx) => {
    if (v.mileageKm.trim() === '') ctx.addIssue({ code: 'custom', path: ['mileageKm'], message: t('edit.required') });
    checkVehicle({ kind: 'vehicle', registeredOn: '', fiscalHp: '', energy: '', ...v }, ctx, t);
  });
  const form = useForm<{ mileageKm: string; mileageOn: string }>({
    resolver: zodResolver(schema), defaultValues: { mileageKm: asset.mileageKm == null ? '' : String(asset.mileageKm), mileageOn: todayIso() },
  });
  const errors = useSubmitErrors(form.setError, (path) => (path === 'mileageKm' || path === 'mileageOn' ? path : undefined), (field) => refusalText(t, field.code));
  const edit = $api.useMutation('put', ASSET);
  const e = form.formState.errors;

  return (
    <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
      event.stopPropagation();
      void form.handleSubmit((values) => {
        errors.clear();
        const all: EditAssetValues = { ...editDefaults(asset), ...values };
        edit.mutate({ params: { path: { id: asset.id } }, body: toEditBody(all, asset.version) }, {
          onSuccess: () => { void invalidateAssets(queryClient); done(t('assets_form.saved')); },
          onError: errors.onError,
        });
      })(event);
    }}>
      <p className="text-muted-foreground">{t('assets_form.mileage_intro')}</p>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t('assets_form.mileage')} error={e.mileageKm?.message}>
          {(c) => <Input inputMode="numeric" {...c} {...form.register('mileageKm')} />}
        </Field>
        <Controller control={form.control} name="mileageOn" render={({ field }) => (
          <Field label={t('assets_form.mileage_on')} error={e.mileageOn?.message}>
            {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
          </Field>
        )} />
      </div>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
        <Button type="submit" disabled={edit.isPending}>{t('assets_form.save')}</Button>
      </div>
    </form>
  );
}
