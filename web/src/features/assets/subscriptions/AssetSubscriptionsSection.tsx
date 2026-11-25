import { useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { Plus } from 'lucide-react';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { invalidateAssets, type AssetView, type LinkedSubscription } from '@/api/assets';
import { present, toProblem } from '@/api/errors';
import { ASSET_LINK, invalidateSubscriptions, LIST } from '@/api/subscriptions';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { FormDialog } from '@/components/FormDialog';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { RowMenu } from '@/components/page/RowMenu';
import { Section } from '@/components/page/Section';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import '@/locales/assets';

const PERIODS = ['day', 'week', 'month', 'year'] as const;

/** #648: the subscriptions linked to the asset that the reader sees (an insurance, a service contract), with « Lier » and « Délier » for a writer. */
export function AssetSubscriptionsSection({ asset }: { asset: AssetView }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [linking, setLinking] = useState(false);
  const unlink = $api.useMutation('put', ASSET_LINK, {
    onSuccess: () => { void invalidateAll(queryClient); toast.success(t('assets_subscriptions.unlinked')); },
  });
  const period = (s: LinkedSubscription) => (PERIODS as readonly (string | null | undefined)[]).includes(s.period)
    ? t('assets_subscriptions.per', { period: t(`assets_subscriptions.period.${s.period as (typeof PERIODS)[number]}`) }) : '';
  return (
    <Section title={t('assets_subscriptions.title')} actions={asset.canWrite ? (
      <Button type="button" variant="outline" size="sm" onClick={() => setLinking(true)}><Plus aria-hidden="true" />{t('assets_subscriptions.link')}</Button>
    ) : undefined}>
      {unlink.error ? <ProblemMessage problem={present(toProblem(unlink.error))} /> : null}
      {asset.subscriptions.length === 0 ? <p className="text-muted-foreground">{t('assets_subscriptions.empty')}</p> : (
        <Table aria-label={t('assets_subscriptions.list')}>
          <TableHeader>
            <TableRow>
              <TableHead>{t('assets_subscriptions.col_name')}</TableHead>
              <TableHead className="text-right">{t('assets_subscriptions.col_price')}</TableHead>
              <TableHead>{t('assets_subscriptions.col_deadline')}</TableHead>
              {asset.canWrite ? <TableHead className="w-0"><span className="sr-only">{t('assets_subscriptions.menu')}</span></TableHead> : null}
            </TableRow>
          </TableHeader>
          <TableBody>
            {asset.subscriptions.map((s) => (
              <TableRow key={s.id}>
                <TableCell className="whitespace-normal font-medium"><Link to="/subscriptions/$id" params={{ id: s.id }} className="underline">{s.name}</Link></TableCell>
                <TableCell className="text-right">{s.price ? <><Money value={s.price} tabular /> {period(s)}</> : '—'}</TableCell>
                <TableCell>{s.nextDeadline ? <LocalDate value={s.nextDeadline} /> : '—'}</TableCell>
                {asset.canWrite ? (
                  <TableCell>
                    <RowMenu label={t('assets_subscriptions.menu')} items={[
                      { label: t('assets_subscriptions.unlink'), onSelect: () => { if (!unlink.isPending) unlink.mutate({ params: { path: { id: s.id } }, body: { assetId: null } }); } },
                    ]} />
                  </TableCell>
                ) : null}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {linking ? (
        <FormDialog title={t('assets_subscriptions.link')} onClose={() => setLinking(false)} onDone={(m) => { setLinking(false); if (m) toast.success(m); }}>
          {({ titleId, done }) => <LinkForm asset={asset} titleId={titleId} onDone={done} />}
        </FormDialog>
      ) : null}
    </Section>
  );
}

const invalidateAll = (queryClient: ReturnType<typeof useQueryClient>) => Promise.all([invalidateAssets(queryClient), invalidateSubscriptions(queryClient)]);

/** The active subscriptions the reader sees, but those already linked here; one linked elsewhere moves to this asset (the PUT replaces). */
function LinkForm({ asset, titleId, onDone }: { asset: AssetView; titleId: string; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<{ subscriptionId: string }>({ defaultValues: { subscriptionId: '' } });
  const list = $api.useQuery('get', LIST, { params: { query: { scope: 'me', status: 'active' } } });
  const taken = new Set(asset.subscriptions.map((s) => s.id));
  const options = (list.data?.items ?? []).filter((s) => !taken.has(s.id)).map((s) => ({ value: s.id, label: s.displayName }));
  const link = $api.useMutation('put', ASSET_LINK);
  return (
    <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(ev) => {
      ev.stopPropagation();
      void form.handleSubmit((values) => {
        if (values.subscriptionId === '') return;
        link.mutate({ params: { path: { id: values.subscriptionId } }, body: { assetId: asset.id } },
          { onSuccess: () => { void invalidateAll(queryClient); onDone(t('assets_subscriptions.linked')); } });
      })(ev);
    }}>
      {link.error ? <ProblemMessage problem={present(toProblem(link.error))} /> : null}
      <Controller control={form.control} name="subscriptionId" render={({ field }) => (
        <Field label={t('assets_subscriptions.pick')} hint={t('assets_subscriptions.pick_hint')}>
          {(c) => <ComboboxField {...c} value={field.value} onValueChange={field.onChange} options={options}
            placeholder={t('assets_subscriptions.pick_placeholder')} searchPlaceholder={t('assets_subscriptions.pick_search')} empty={t('assets_subscriptions.pick_empty')} />}
        </Field>
      )} />
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('common.cancel')}</Button>
        <Button type="submit" disabled={link.isPending}>{t('assets_subscriptions.submit')}</Button>
      </div>
    </form>
  );
}
