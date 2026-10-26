import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { ConfirmAction } from '@/components/ConfirmAction';
import { SelectField } from '@/components/form/SelectField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { useSetLinkRole, useUnlinkDocument, type DocumentLink, type DocumentSummary } from './api';
import { linkKey, roleText, ROLES } from './format';
import '@/locales/documents';

/** One row per link (the row unmounts once removed, so the page is told to move the focus; a failure shows in the row): its label, then its role (a select when the actor may write its target) and « Retirer » behind a confirmation. */
export function DocumentLinks({ doc, onRemoved }: { doc: DocumentSummary; onRemoved: () => void }) {
  const { t } = useTranslation();
  if (doc.links.length === 0) return <p className="text-muted-foreground">{t('documents_page.links.none')}</p>;
  return (
    <ul aria-label={t('documents_page.links.list')} className="divide-y rounded-md border">
      {doc.links.map((l) => <LinkRow key={linkKey(l)} doc={doc} link={l} onRemoved={onRemoved} />)}
    </ul>
  );
}

function LinkRow({ doc, link, onRemoved }: { doc: DocumentSummary; link: DocumentLink; onRemoved: () => void }) {
  const { t } = useTranslation();
  const id = useId();
  const setRole = useSetLinkRole();
  const unlink = useUnlinkDocument();
  const path = { params: { path: { id: doc.id, targetKind: link.targetKind, targetId: link.targetId } } };
  const error = setRole.error ?? unlink.error;
  return (
    <li className="flex flex-wrap items-center justify-between gap-3 p-3">
      <span className="font-medium">{link.label}</span>
      {link.canWrite ? (
        <span className="flex flex-wrap items-center gap-2">
          <label htmlFor={id} className="sr-only">{t('documents_page.links.role_of', { label: link.label })}</label>
          <span className="w-56">
            <SelectField id={id} value={link.role} disabled={setRole.isPending}
              onValueChange={(role) => setRole.mutate({ ...path, body: { role } })}
              options={ROLES.map((r) => ({ value: r, label: roleText(t, r) }))} />
          </span>
          <ConfirmAction label={t('documents_page.links.remove')} pending={unlink.isPending}
            confirmation={t('documents_page.links.remove_confirmation', { label: link.label })} onConfirm={() => void unlink.mutateAsync(path).then(onRemoved, () => undefined)} />
        </span>
      ) : <span className="text-muted-foreground">{roleText(t, link.role)}</span>}
      {error ? <div className="w-full"><ProblemMessage problem={present(toProblem(error))} /></div> : null}
    </li>
  );
}
