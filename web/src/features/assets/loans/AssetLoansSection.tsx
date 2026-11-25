import { useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { Plus } from 'lucide-react';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { invalidateAssets, type AssetView } from '@/api/assets';
import { present, toProblem } from '@/api/errors';
import { invalidateLoans, LOAN, LOANS } from '@/api/loans';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { FormDialog } from '@/components/FormDialog';
import { Money } from '@/components/Money';
import { RowMenu } from '@/components/page/RowMenu';
import { Section } from '@/components/page/Section';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import '@/locales/assets';

const invalidateAll = (queryClient: ReturnType<typeof useQueryClient>) => Promise.all([invalidateAssets(queryClient), invalidateLoans(queryClient)]);

/**
 * #188: the readable loans financing the asset with the capital still owed, and the net value (the server's figure: the value less
 * those balances). « Lier un crédit » and « Délier » go through the loan's own route.
 */
export function AssetLoansSection({ asset }: { asset: AssetView }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [linking, setLinking] = useState(false);
  const unlink = $api.useMutation('patch', LOAN, {
    onSuccess: () => { void invalidateAll(queryClient); toast.success(t('assets_loans.unlinked')); },
  });
  const loans = asset.loans ?? [];
  return (
    <Section title={t('assets_loans.title')} actions={asset.canWrite ? (
      <Button type="button" variant="outline" size="sm" onClick={() => setLinking(true)}><Plus aria-hidden="true" />{t('assets_loans.link')}</Button>
    ) : undefined}>
      {unlink.error ? <ProblemMessage problem={present(toProblem(unlink.error))} /> : null}
      {loans.length === 0 ? <p className="text-muted-foreground">{t('assets_loans.empty')}</p> : (
        <>
          {asset.fxMissing ? <p role="status" className="text-status-warning-text">{t('assets_loans.fx_missing')}</p> : null}
          <StatLine items={[
            { label: t('assets_loans.value'), value: asset.value ? <Money value={asset.value} /> : '—' },
            { label: t('assets_loans.net'), hint: t('assets_loans.net_hint'), value: asset.netValue ? <Money value={asset.netValue} /> : '—' },
          ]} />
          <Table aria-label={t('assets_loans.list')}>
            <TableHeader>
              <TableRow>
                <TableHead>{t('assets_loans.col_name')}</TableHead>
                <TableHead className="text-right">{t('assets_loans.col_balance')}</TableHead>
                {asset.canWrite ? <TableHead className="w-0"><span className="sr-only">{t('assets_loans.menu')}</span></TableHead> : null}
              </TableRow>
            </TableHeader>
            <TableBody>
              {loans.map((l) => (
                <TableRow key={l.id}>
                  <TableCell className="whitespace-normal font-medium"><Link to="/loans/$id" params={{ id: l.id }} className="underline">{l.name}</Link></TableCell>
                  <TableCell className="text-right">{l.balance ? <Money value={l.balance} tabular /> : <span className="text-muted-foreground">{t('assets_loans.no_rate')}</span>}</TableCell>
                  {asset.canWrite ? (
                    <TableCell>
                      <RowMenu label={t('assets_loans.menu')} items={[
                        { label: t('assets_loans.unlink'), onSelect: () => {
                          if (!unlink.isPending) unlink.mutate({ params: { path: { id: l.id } }, body: { clearLender: false, clearNote: false, clearProperty: false, clearEnvelope: false, clearAsset: true } });
                        } },
                      ]} />
                    </TableCell>
                  ) : null}
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </>
      )}
      {linking ? (
        <FormDialog title={t('assets_loans.link')} onClose={() => setLinking(false)} onDone={(m) => { setLinking(false); if (m) toast.success(m); }}>
          {({ titleId, done }) => <LinkForm asset={asset} titleId={titleId} onDone={done} />}
        </FormDialog>
      ) : null}
    </Section>
  );
}

/** The unpaid loans the reader can edit that finance nothing (the server's `unfinanced` and `writable` filters): a loan is never moved from another item. */
function LinkForm({ asset, titleId, onDone }: { asset: AssetView; titleId: string; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<{ loanId: string }>({ defaultValues: { loanId: '' } });
  const list = $api.useQuery('get', LOANS, { params: { query: { scope: 'me', unfinanced: true, writable: true } } });
  const options = (list.data?.items ?? []).map((l) => ({ value: l.id, label: l.name }));
  const link = $api.useMutation('patch', LOAN);
  return (
    <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(ev) => {
      ev.stopPropagation();
      void form.handleSubmit((values) => {
        if (values.loanId === '') return;
        link.mutate({ params: { path: { id: values.loanId } }, body: { clearLender: false, clearNote: false, clearProperty: false, clearEnvelope: false, clearAsset: false, assetId: asset.id } },
          { onSuccess: () => { void invalidateAll(queryClient); onDone(t('assets_loans.linked')); } });
      })(ev);
    }}>
      {link.error ? (toProblem(link.error).status === 403
        ? <p role="alert" className="text-destructive">{t('assets_loans.forbidden')}</p>
        : <ProblemMessage problem={present(toProblem(link.error))} />) : null}
      {options.length === 0 && !list.isPending ? <p className="text-muted-foreground">{t('assets_loans.pick_empty')}</p> : (
        <Controller control={form.control} name="loanId" render={({ field }) => (
          <Field label={t('assets_loans.pick')}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange} options={options} placeholder={t('assets_loans.pick_placeholder')} />}
          </Field>
        )} />
      )}
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('common.cancel')}</Button>
        <Button type="submit" disabled={link.isPending || options.length === 0}>{t('assets_loans.submit')}</Button>
      </div>
    </form>
  );
}
