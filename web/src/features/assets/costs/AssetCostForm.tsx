import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ASSET_COST, ASSET_COSTS, invalidateAssets, type AssetCost, type AssetView } from '@/api/assets';
import { present, toProblem } from '@/api/errors';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { Reload } from '@/components/form/Reload';
import { SelectField } from '@/components/form/SelectField';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { ModalDialog } from '@/components/ModalDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { invalidateDocuments } from '@/api/documents';
import { uploadDocument, useAnchoredDocuments } from '@/features/documents/api';
import { UploadZone } from '@/features/documents/DocumentUpload';
import { todayIso } from '@/lib/today';
import { ASSET_COST_CATEGORIES } from '@/api/assets';
import { COST_PATHS, costSchema, costValuesOf, toCostBody, type CostValues } from './cost';
import '@/locales/assets';
import '@/locales/documents';
import '@/locales/loans';

/**
 * #649: a running cost added (`editing` absent: POST) or corrected (PUT: the server supersedes it, so the row comes back under a new id)
 * in a dialog. The invoice is a document of the vehicle or one dropped here, which is uploaded to the vehicle as an invoice first.
 */
export function AssetCostDialog({ asset, editing, onClose, onDone }: {
  asset: AssetView; editing?: AssetCost; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const dialog = useRef<{ close: () => void }>(null);
  return (
    <ModalDialog title={t(editing ? 'assets_costs.edit_title' : 'assets_costs.add_title')} onClose={onClose} dialogRef={dialog}>
      {(titleId) => (
        <CostForm asset={asset} editing={editing} titleId={titleId}
          onDone={(message) => { if (message) { dialog.current?.close(); onDone(message); } else onClose(); }} />
      )}
    </ModalDialog>
  );
}

function CostForm({ asset, editing, titleId, onDone }: { asset: AssetView; editing?: AssetCost; titleId: string; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api, fetchClient } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<CostValues>({ resolver: zodResolver(costSchema(t)), defaultValues: costValuesOf(editing) });
  const errors = useSubmitErrors(form.setError, COST_PATHS);
  const add = $api.useMutation('post', ASSET_COSTS);
  const correct = $api.useMutation('put', ASSET_COST);
  const documents = useAnchoredDocuments(asset.id);
  const [uploaded, setUploaded] = useState<{ id: string; title: string }[]>([]);
  const [uploading, setUploading] = useState(false);
  const [uploadProblem, setUploadProblem] = useState<string | null>(null);

  const known = [...(documents.data?.items ?? []).map((d) => ({ id: d.id, title: d.title })), ...uploaded.filter((u) => !documents.data?.items.some((d) => d.id === u.id))];
  // « Aucune facture » on a stored invoice clears it; an invoice the reader cannot see is not shown, so it is neither offered nor cleared.
  const options = [{ value: '', label: t('assets_costs.no_invoice') }, ...known.map((d) => ({ value: d.id, label: d.title }))];

  const upload = async (file: File) => {
    setUploading(true);
    setUploadProblem(null);
    try {
      const { id } = await uploadDocument(fetchClient, file, { targetKind: 'core.item', targetId: asset.id, role: 'invoice' });
      setUploaded((list) => [...list, { id, title: file.name }]);
      form.setValue('documentId', id);
      void invalidateDocuments(queryClient);
    } catch (error) {
      setUploadProblem(present(toProblem(error)).detail ?? t('assets_costs.upload_failed'));
    } finally {
      setUploading(false);
    }
  };

  const submit = form.handleSubmit((values) => {
    errors.clear();
    const callbacks = {
      onSuccess: () => { void invalidateAssets(queryClient); onDone(t(editing ? 'assets_costs.corrected' : 'assets_costs.added')); },
      onError: (error: unknown) => errors.onError(error),
    };
    if (editing) correct.mutate({ params: { path: { id: asset.id, flowId: editing.id } }, body: toCostBody(values, editing) }, callbacks);
    else add.mutate({ params: { path: { id: asset.id } }, body: toCostBody(values) }, callbacks);
  });

  const e = form.formState.errors;
  const problem = errors.problem;
  const gone = (editing ? correct.error : add.error) ? ['conflict', 'not_found'].includes(present(toProblem(editing ? correct.error : add.error)).kind) : false;

  return (
    <form aria-labelledby={titleId} noValidate onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3">
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {gone ? <Reload onReload={() => invalidateAssets(queryClient)} /> : null}
        </>
      ) : null}
      <div className="grid gap-3 sm:grid-cols-2">
        <Controller control={form.control} name="category" render={({ field }) => (
          <Field label={t('assets_costs.category')} error={e.category?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
              options={ASSET_COST_CATEGORIES.map((k) => ({ value: k, label: t(`assets_costs.category_option.${k}`) }))} />}
          </Field>
        )} />
        <Controller control={form.control} name="occurredOn" render={({ field }) => (
          <Field label={t('assets_costs.date')} error={e.occurredOn?.message}>
            {(c) => <DateField {...c} value={field.value} onChange={field.onChange} max={todayIso()} />}
          </Field>
        )} />
        <Field label={t('assets_costs.amount')} error={e.amount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
        </Field>
      </div>
      <Field label={t('assets_costs.note')} error={e.note?.message}>
        {(c) => <Textarea {...c} {...form.register('note')} />}
      </Field>
      <Controller control={form.control} name="documentId" render={({ field }) => (
        <Field label={t('assets_costs.invoice')} hint={t('assets_costs.invoice_hint')} error={e.documentId?.message ?? uploadProblem ?? undefined}>
          {(c) => (
            <div className="space-y-2">
              <SelectField {...c} value={field.value} onValueChange={field.onChange} options={options} />
              <UploadZone multiple={false} disabled={uploading} pick={t('assets_costs.attach')} hint={t('documents_page.upload.types')}
                onFiles={(files) => { if (files[0]) void upload(files[0]); }} onRefused={() => setUploadProblem(t('assets_costs.upload_failed'))} />
            </div>
          )}
        </Field>
      )} />
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('common.cancel')}</Button>
        <Button type="submit" disabled={add.isPending || correct.isPending || uploading}>{t(editing ? 'assets_costs.save' : 'assets_costs.submit')}</Button>
      </div>
    </form>
  );
}
