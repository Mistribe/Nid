import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { previewLink, useDocuments, useLinkDocument } from './api';
import { roleText, ROLES } from './format';
import '@/locales/documents';

/**
 * An existing document linked to the item, as `AddLinkDialog` does from the document's side: the candidates are the
 * readable, unarchived documents the actor may edit and that have no link to this item yet (a link to one of its phases
 * does not count). The preview first; if it names anyone, nothing is linked until « Relier quand même ».
 */
export function ItemLinkDialog({ itemId, defaultRole, onClose, onDone }: {
  itemId: string; defaultRole: string; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { fetchClient } = useApi();
  const docs = useDocuments('me', false, false);
  const link = useLinkDocument();
  const [docId, setDocId] = useState('');
  const [role, setRole] = useState(defaultRole);
  // The names a confirmed link would let read the document; null until the preview widened it.
  const [gained, setGained] = useState<string[] | null>(null);
  const [checking, setChecking] = useState(false);
  const [problem, setProblem] = useState<unknown>(null);
  const offered = (docs.data?.items ?? []).filter((d) => !d.archived && d.canEdit
    && !d.links.some((l) => l.targetKind === 'core.item' && l.targetId === itemId));
  const pick = (set: (v: string) => void) => (v: string) => { set(v); setGained(null); setProblem(null); };

  const add = async (done: (message?: string) => void) => {
    if (!docId || !role) return;
    const post = () => link.mutate({ params: { path: { id: docId } }, body: { targetKind: 'core.item', targetId: itemId, role } },
      { onSuccess: () => done(t('documents_item.linked')), onError: setProblem });
    if (gained) { post(); return; }
    setProblem(null);
    setChecking(true);
    try {
      const { gainedReaders } = await previewLink(fetchClient, docId, 'core.item', itemId);
      if (gainedReaders.length === 0) post();
      else setGained(gainedReaders.map((r) => r.name));
    } catch (error) {
      setProblem(error);
    } finally {
      setChecking(false);
    }
  };

  return (
    <FormDialog title={t('documents_item.link_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => { event.preventDefault(); event.stopPropagation(); void add(done); }}>
          {problem ? <ProblemMessage problem={present(toProblem(problem))} /> : null}
          {docs.error ? <ProblemMessage problem={present(toProblem(docs.error))} /> : null}
          <Field label={t('documents_item.document')}>
            {(control) => <ComboboxField {...control} value={docId} onValueChange={pick(setDocId)} placeholder={t('documents_item.document_pick')}
              searchPlaceholder={t('documents_item.document_search')} empty={t('documents_item.document_empty')}
              options={offered.map((d) => ({ value: d.id, label: d.title }))} />}
          </Field>
          <Field label={t('documents_item.col_role')}>
            {(control) => <SelectField {...control} value={role} onValueChange={pick(setRole)} options={ROLES.map((r) => ({ value: r, label: roleText(t, r) }))} />}
          </Field>
          {gained ? (
            <p role="alert" className="rounded-md border border-status-warning/40 bg-status-warning/10 p-3 text-sm text-status-warning-text">
              {t('documents_page.links.widens', { count: gained.length, names: new Intl.ListFormat('fr', { type: 'conjunction' }).format(gained) })}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('documents_page.links.cancel')}</Button>
            <Button type="submit" disabled={!docId || !role || checking || link.isPending}>
              {t(gained ? 'documents_item.link_anyway' : 'documents_item.link_submit')}
            </Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
