import { useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ASSETS } from '@/api/assets';
import { ASSET_LINK, invalidateSubscriptions, type Detail } from '@/api/subscriptions';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { Section } from '@/components/page/Section';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import '@/locales/subscriptions';

/** #648: the asset the subscription relates to, when the reader sees it: a link to its page. A hidden asset has no name and no link. */
export function AssetLinkSection({ detail }: { detail: Detail }) {
  const { t } = useTranslation();
  if (!detail.asset) return null;
  return (
    <Section title={t('asset_link.title')}>
      <Link to="/other-assets/$id" params={{ id: detail.asset.id }} className="underline">{detail.asset.name}</Link>
    </Section>
  );
}

/** #648: « Bien concerné » in a dialog: the asset picked is the whole answer (PUT), none unlinks. */
export function AssetLinkForm({ detail, titleId, onDone }: { detail: Detail; titleId: string; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<{ assetId: string }>({ defaultValues: { assetId: detail.asset?.id ?? '' } });
  const errors = useSubmitErrors(form.setError, (path) => (path === 'assetId' ? 'assetId' : undefined),
    (field) => (field.code === 'subscriptions.invalid_asset' ? t('asset_link.invalid_asset') : undefined));
  const assets = $api.useQuery('get', ASSETS, { params: { query: { scope: 'me' } } });
  const rows = assets.data?.rows ?? [];
  const linked = detail.asset ?? null;
  const options = [
    { value: '', label: t('asset_link.none') },
    ...(linked && !rows.some((a) => a.id === linked.id) ? [{ value: linked.id, label: linked.name }] : []),
    ...rows.map((a) => ({ value: a.id, label: a.name })),
  ];
  const save = $api.useMutation('put', ASSET_LINK, {
    onSuccess: () => { void invalidateSubscriptions(queryClient); onDone(t('asset_link.saved')); },
    onError: (error) => errors.onError(error),
  });
  return (
    <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(ev) => {
      ev.stopPropagation();
      void form.handleSubmit((values) => {
        errors.clear();
        save.mutate({ params: { path: { id: detail.id } }, body: { assetId: values.assetId === '' ? null : values.assetId } });
      })(ev);
    }}>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <Controller control={form.control} name="assetId" render={({ field }) => (
        <Field label={t('asset_link.field')} error={form.formState.errors.assetId?.message}
          hint={assets.isSuccess && rows.length === 0 && !linked ? t('asset_link.hint_empty') : t('asset_link.hint')}>
          {(c) => <ComboboxField {...c} value={field.value} onValueChange={field.onChange} options={options}
            placeholder={t('asset_link.placeholder')} searchPlaceholder={t('asset_link.search')} empty={t('asset_link.empty')} />}
        </Field>
      )} />
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('common.cancel')}</Button>
        <Button type="submit" disabled={save.isPending}>{t('asset_link.submit')}</Button>
      </div>
    </form>
  );
}
