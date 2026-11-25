import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { useId, useState } from 'react';
import { Controller, FormProvider, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ASSETS, invalidateAssets } from '@/api/assets';
import { PERSONS } from '@/api/persons';
import { useSession } from '@/app/Providers';
import { CurrencyField } from '@/components/form/CurrencyField';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { Folded } from '@/components/form/Folded';
import { Segmented } from '@/components/form/Segmented';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { OwnershipFields } from '@/components/ownership/OwnershipFields';
import { FormActions, FormPage } from '@/components/page/FormPage';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { todayIso } from '@/lib/today';
import { AssetFields, NoteField, VehicleFields } from '../AssetFields';
import { newAssetPath, newAssetSchema, refusalText, toCreateBody, type NewAssetValues } from '../assetForm';
import '@/locales/assets';
import '@/locales/loans';

/** One page, one request: what the asset is, what it is worth (the purchase price or today's value), who owns it. */
export function NewAsset() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const people = $api.useQuery('get', PERSONS);
  const valueId = useId();
  const visibilityId = useId();
  const visibilityErrorId = useId();
  const form = useForm<NewAssetValues>({
    resolver: zodResolver(newAssetSchema(t)),
    // `visibility` is left out on purpose: neither answer is checked until the person picks one.
    defaultValues: {
      name: '', kind: 'vehicle', currency: 'EUR', note: '', weightGrams: '', fineness: '', registeredOn: '', mileageKm: '', mileageOn: '', fiscalHp: '', energy: '', mode: 'purchase', amount: '', valueOn: '',
      owners: [{ personId: session.person.id, share: '100' }],
    },
  });
  const errors = useSubmitErrors(form.setError, newAssetPath, (field) => refusalText(t, field.code));
  const create = $api.useMutation('post', ASSETS, {
    onSuccess: ({ id }) => {
      void invalidateAssets(queryClient);
      void navigate({ to: '/other-assets/$id', params: { id } });
    },
    onError: errors.onError,
  });
  const e = form.formState.errors;
  const mode = form.watch('mode');
  // A lease is a contract, not an asset: it goes to the subscriptions, and nothing is created here.
  // No answer is preselected (like the visibility): a vehicle's form continues once the household has picked one.
  const [ownership, setOwnership] = useState<'owned' | 'leased' | null>(null);
  const [unanswered, setUnanswered] = useState(false);
  const isVehicle = form.watch('kind') === 'vehicle';
  const isLeased = isVehicle && ownership === 'leased';
  const ownershipId = useId();
  const ownershipErrorId = useId();
  const vehicleError = Boolean(e.registeredOn || e.mileageKm || e.mileageOn || e.fiscalHp || e.energy);

  return (
    <FormPage crumbs={[{ label: t('nav.assets'), to: '/other-assets' }]} title={t('assets_form.title')}>
      <p className="text-muted-foreground">{t('assets_form.intro')}</p>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <FormProvider {...form}>
        <form noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          if (isLeased) return; // a lease creates nothing here, whatever key sent the form
          if (isVehicle && ownership === null) { setUnanswered(true); return; }
          void form.handleSubmit((values) => { errors.clear(); create.mutate({ body: toCreateBody(values) }); })(event);
        }}>
          <AssetFields />
          {isVehicle ? (
            <div className="space-y-1">
              <span id={ownershipId} className="block text-sm font-medium">{t('assets_form.owned_or_leased')}</span>
              <Segmented aria-labelledby={ownershipId} aria-describedby={unanswered && ownership === null ? ownershipErrorId : undefined}
                aria-invalid={unanswered && ownership === null ? true : undefined} value={ownership ?? ''}
                onValueChange={(v) => { setOwnership(v === 'leased' ? 'leased' : 'owned'); setUnanswered(false); }}
                options={[{ value: 'owned', label: t('assets_form.owned') }, { value: 'leased', label: t('assets_form.leased') }]} />
              {unanswered && ownership === null ? <p id={ownershipErrorId} className="text-sm text-destructive">{t('assets_form.owned_or_leased_required')}</p> : null}
            </div>
          ) : null}
          {isLeased ? (
            <div className="space-y-3">
              <p>{t('assets_form.leased_explain')}</p>
              <Button asChild>
                <Link to="/subscriptions/new" search={{ ...(form.watch('name').trim() ? { name: form.watch('name').trim() } : {}), note: t('assets_form.leased_note') }}>
                  {t('assets_form.leased_link')}
                </Link>
              </Button>
            </div>
          ) : isVehicle && ownership === null ? null : (
          <>
          {isVehicle ? <Folded label={t('assets_form.vehicle_details')} hasError={vehicleError}><VehicleFields /></Folded> : null}
          <div className="space-y-2">
            <span id={valueId} className="block text-sm font-medium">{t('assets_form.value_title')}</span>
            <Controller control={form.control} name="mode" render={({ field }) => (
              <Segmented aria-labelledby={valueId} value={field.value} onValueChange={field.onChange}
                options={[{ value: 'purchase', label: t('assets_form.value_purchase') }, { value: 'current', label: t('assets_form.value_current') }]} />
            )} />
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label={t('assets_form.amount')} error={e.amount?.message}>
                {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
              </Field>
              <Controller control={form.control} name="valueOn" render={({ field }) => (
                <Field label={t('assets_form.date')} hint={t(mode === 'purchase' ? 'assets_form.date_hint_purchase' : 'assets_form.date_hint_current')} error={e.valueOn?.message}>
                  {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
                </Field>
              )} />
            </div>
          </div>
          <div className="space-y-1">
            <span id={visibilityId} className="block text-sm">{t('assets_form.visibility')}</span>
            <Controller control={form.control} name="visibility" render={({ field }) => (
              <Segmented aria-labelledby={visibilityId} aria-describedby={e.visibility ? visibilityErrorId : undefined}
                aria-invalid={e.visibility ? true : undefined} value={field.value ?? ''} onValueChange={field.onChange}
                options={(['shared', 'private'] as const).map((v) => ({ value: v, label: t(`loan_form.visibility_${v}`) }))} />
            )} />
            {e.visibility ? <p id={visibilityErrorId} className="text-sm text-destructive">{e.visibility.message}</p> : null}
          </div>
          <OwnershipFields form={form} persons={people.data ?? []} withVisibility={false} />
          <Folded label={t('loan_form.more_options')} hasError={Boolean(e.currency || e.note)}>
            <Controller control={form.control} name="currency" render={({ field }) => (
              <Field label={t('assets_form.currency')} error={e.currency?.message}>
                {(c) => <CurrencyField {...c} value={field.value} onValueChange={field.onChange} />}
              </Field>
            )} />
            <NoteField />
          </Folded>
          <FormActions><Button type="submit" disabled={create.isPending}>{t('assets_form.submit')}</Button></FormActions>
          </>
          )}
        </form>
      </FormProvider>
    </FormPage>
  );
}
