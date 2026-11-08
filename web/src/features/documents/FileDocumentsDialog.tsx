import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { invalidateDocuments, type DocumentSummary } from '@/api/documents';
import { linkDocument, previewLink, useDocumentTargets } from './api';
import { linkKey, roleText, ROLES } from './format';
import '@/locales/documents';

const keyOf = (t: { kind: string; id: string }) => linkKey({ targetKind: t.kind, targetId: t.id });

/** What « Classer » did: the documents linked, and those the server refused with why; nothing is lost between the two. */
export type Filed = { filed: string[]; refused: { id: string; problem: unknown }[] };

/**
 * One element and one role for every selected document (issue #183), the same fields as « Ajouter un lien ». The previews
 * run first, one per document: their gained readers are named together and nothing is written until « Classer quand
 * même ». Each document is then linked on its own, as from its page, so a refused one leaves the others filed.
 */
export function FileDocumentsDialog({ docs, onClose, onDone }: { docs: DocumentSummary[]; onClose: () => void; onDone: (message: string, outcome: Filed) => void }) {
  const { t } = useTranslation();
  const { fetchClient } = useApi();
  const queryClient = useQueryClient();
  const targets = useDocumentTargets();
  const [target, setTarget] = useState('');
  const [role, setRole] = useState('');
  // Once a preview widened the documents: the names to confirm, the documents to link and those already refused.
  const [pending, setPending] = useState<{ names: string[]; ids: string[]; refused: Filed['refused'] } | null>(null);
  const [busy, setBusy] = useState(false);
  const outcome = useRef<Filed>({ filed: [], refused: [] });
  const offered = (targets.data?.items ?? []).filter((x) => x.canWrite || x.kind !== 'core.person'); // « lecture seule » on an adult would read as a read-only item
  const chosen = offered.find((x) => keyOf(x) === target);
  const pick = (set: (v: string) => void) => (v: string) => { set(v); setPending(null); };
  const results = <T,>(ids: string[], settled: PromiseSettledResult<T>[]) => ({
    ok: ids.flatMap((id, i) => (settled[i]!.status === 'fulfilled' ? [{ id, value: (settled[i] as PromiseFulfilledResult<T>).value }] : [])),
    refused: ids.flatMap((id, i) => (settled[i]!.status === 'rejected' ? [{ id, problem: (settled[i] as PromiseRejectedResult).reason as unknown }] : [])),
  });

  const write = async (ids: string[], refused: Filed['refused'], done: (message?: string) => void) => {
    if (!chosen) return;
    const linked = results(ids, await Promise.allSettled(ids.map((id) => linkDocument(fetchClient, id, chosen.kind, chosen.id, role))));
    await invalidateDocuments(queryClient);
    outcome.current = { filed: linked.ok.map((x) => x.id), refused: [...refused, ...linked.refused] };
    const count = outcome.current.filed.length;
    done(count === 0 ? t('documents_page.file.none') : t('documents_page.file.filed', { count }));
  };

  const submit = async (done: (message?: string) => void) => {
    if (!chosen || !role || busy) return;
    setBusy(true);
    try {
      if (pending) { await write(pending.ids, pending.refused, done); return; }
      const ids = docs.map((d) => d.id);
      const previews = results(ids, await Promise.allSettled(ids.map((id) => previewLink(fetchClient, id, chosen.kind, chosen.id))));
      const names = [...new Map(previews.ok.flatMap((p) => p.value.gainedReaders).map((r) => [r.id, r.name])).values()];
      if (names.length === 0 || previews.ok.length === 0) await write(previews.ok.map((p) => p.id), previews.refused, done);
      else setPending({ names, ids: previews.ok.map((p) => p.id), refused: previews.refused });
    } finally {
      setBusy(false);
    }
  };

  const names = pending ? new Intl.ListFormat('fr', { type: 'conjunction' }).format(pending.names) : '';
  return (
    <FormDialog title={t('documents_page.file.title', { count: docs.length })} onClose={onClose} onDone={(message) => onDone(message, outcome.current)}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => { event.preventDefault(); event.stopPropagation(); void submit(done); }}>
          {targets.error ? <ProblemMessage problem={present(toProblem(targets.error))} /> : null}
          <Field label={t('documents_page.links.target')}>
            {(control) => <ComboboxField {...control} value={target} onValueChange={pick(setTarget)} placeholder={t('documents_page.links.target_pick')}
              searchPlaceholder={t('documents_page.links.target_search')} empty={t('documents_page.links.target_empty')}
              options={offered.map((x) => ({ value: keyOf(x), disabled: !x.canWrite,
                label: x.canWrite ? x.label : t('documents_page.links.read_only', { label: x.label }) }))} />}
          </Field>
          <Field label={t('documents_page.links.role')}>
            {(control) => <SelectField {...control} value={role} onValueChange={pick(setRole)} placeholder={t('documents_page.links.role_pick')}
              options={ROLES.map((r) => ({ value: r, label: roleText(t, r) }))} />}
          </Field>
          {pending ? (
            <p role="alert" className="rounded-md border border-status-warning/40 bg-status-warning/10 p-3 text-sm text-status-warning-text">
              {docs.length === 1
                ? t('documents_page.links.widens', { count: pending.names.length, names })
                : t('documents_page.file.widens', { count: pending.names.length, names })}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('documents_page.links.cancel')}</Button>
            <Button type="submit" disabled={!chosen || !role || busy}>
              {t(pending ? 'documents_page.file.confirm' : 'documents_page.file.submit')}
            </Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
