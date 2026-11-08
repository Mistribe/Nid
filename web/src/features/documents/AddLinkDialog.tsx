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
import { type DocumentSummary } from '@/api/documents';
import { previewLink, useDocumentTargets, useLinkDocument } from './api';
import { linkKey, roleText, ROLES } from './format';
import '@/locales/documents';

const keyOf = (t: { kind: string; id: string }) => linkKey({ targetKind: t.kind, targetId: t.id });

/**
 * An element and a role (design §2.2). Before adding, the preview says who would gain the document: nobody, it is added
 * at once; someone, their names are shown and nothing is added until « Ajouter quand même ».
 */
export function AddLinkDialog({ doc, onClose, onDone }: { doc: DocumentSummary; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { fetchClient } = useApi();
  const targets = useDocumentTargets();
  const link = useLinkDocument();
  const [target, setTarget] = useState('');
  const [role, setRole] = useState('');
  // The names a confirmed link would let read the document; null until the preview widened it.
  const [gained, setGained] = useState<string[] | null>(null);
  const [checking, setChecking] = useState(false);
  const [problem, setProblem] = useState<unknown>(null);
  const linked = new Set(doc.links.map(linkKey));
  const offered = (targets.data?.items ?? []).filter((x) => !linked.has(keyOf(x)) && (x.canWrite || x.kind !== 'core.person')); // « lecture seule » on an adult would read as a read-only item
  const chosen = offered.find((x) => keyOf(x) === target);
  const pick = (set: (v: string) => void) => (v: string) => { set(v); setGained(null); setProblem(null); };

  const add = async (done: (message?: string) => void) => {
    if (!chosen || !role) return;
    const post = () => link.mutate({ params: { path: { id: doc.id } }, body: { targetKind: chosen.kind, targetId: chosen.id, role } },
      { onSuccess: () => done(t('documents_page.links.added')), onError: setProblem });
    if (gained) { post(); return; }
    setProblem(null);
    setChecking(true);
    try {
      const { gainedReaders } = await previewLink(fetchClient, doc.id, chosen.kind, chosen.id);
      if (gainedReaders.length === 0) post();
      else setGained(gainedReaders.map((r) => r.name));
    } catch (error) {
      setProblem(error);
    } finally {
      setChecking(false);
    }
  };

  return (
    <FormDialog title={t('documents_page.links.add')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => { event.preventDefault(); event.stopPropagation(); void add(done); }}>
          {problem ? <ProblemMessage problem={present(toProblem(problem))} /> : null}
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
          {gained ? (
            <p role="alert" className="rounded-md border border-status-warning/40 bg-status-warning/10 p-3 text-sm text-status-warning-text">
              {t('documents_page.links.widens', { count: gained.length, names: new Intl.ListFormat('fr', { type: 'conjunction' }).format(gained) })}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('documents_page.links.cancel')}</Button>
            <Button type="submit" disabled={!chosen || !role || checking || link.isPending}>
              {t(gained ? 'documents_page.links.confirm' : 'documents_page.links.submit')}
            </Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
