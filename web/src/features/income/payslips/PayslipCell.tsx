import { useNavigate } from '@tanstack/react-router';
import { PaperclipIcon } from 'lucide-react';
import { lazy, Suspense, useRef, useState, type Ref } from 'react';
import { useTranslation } from 'react-i18next';
import { present } from '@/api/errors';
import { RowMenu } from '@/components/page/RowMenu';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { contentUrl, type DocumentLink, type DocumentSummary } from '../../documents/api';
import { ACCEPT, refusal, useDropFiles } from '../../documents/DocumentUpload';
import { previewable, usePreview } from '../../documents/preview';
import type { IncomeSourceDetail } from '../api';
import { linksOnSource, payslipTitle, type MonthKey } from './payslip';
import { usePayslipWrites, type AttachFields, type PayslipWrites } from './usePayslipWrites';
import '@/locales/documents';
import '@/locales/income';

const ConfirmDialog = lazy(() => import('@/components/ConfirmDialog'));

/** A month's payslips (each with its menu), « Pas de bulletin » when it is missing, and « Joindre » while it has none. */
export function PayslipCell({ source, month, docs, missing, target, writable }: {
  source: IncomeSourceDetail; month: MonthKey; docs: DocumentSummary[]; missing: boolean;
  target: { phaseId: string; date: string } | null; writable: boolean;
}) {
  const { t } = useTranslation();
  const writes = usePayslipWrites();
  const input = useRef<HTMLInputElement>(null);
  const phaseIds = new Set(source.phases.map((p) => p.id));
  const fields: AttachFields | null = target && { target: { kind: 'income.phase', id: target.phaseId }, date: target.date, title: payslipTitle(t, month) };
  const attach = (file: File) => { if (fields) void writes.attach(file, fields.target, fields.date, fields.title); };
  const drop = useDropFiles({ multiple: false, disabled: writes.pending, onFiles: ([file]) => attach(file), onRefused: writes.refuse });
  return (
    <div className="space-y-1">
      {docs.length > 0 ? (
        <ul className="space-y-1">
          {docs.map((doc) => (
            <li key={doc.id} className="flex items-center gap-1">
              <PayslipLink doc={doc} />
              <PayslipMenu doc={doc} links={linksOnSource(doc, source.id, phaseIds)} replaceWith={fields} writable={writable} writes={writes} />
            </li>
          ))}
        </ul>
      ) : null}
      {missing ? <p className="text-muted-foreground">{t('income_detail.payslips.missing')}</p> : null}
      {docs.length === 0 && writable && fields ? (
        <div {...drop.handlers} className={cn('w-fit rounded-md', drop.over && 'bg-accent ring-2 ring-primary')}>
          <Button variant="outline" size="sm" disabled={writes.pending} onClick={() => input.current?.click()}>{t('income_detail.payslips.attach')}</Button>
          <FileInput ref={input} label={t('income_detail.payslips.attach')} onFile={attach} />
        </div>
      ) : null}
      <PayslipProblem writes={writes} />
    </div>
  );
}

/** The paperclip and the title, a download link. */
export function PayslipLink({ doc }: { doc: DocumentSummary }) {
  return (
    <a href={contentUrl(doc.id)} download className="inline-flex items-center gap-1 underline-offset-4 hover:underline">
      <PaperclipIcon aria-hidden="true" className="size-4 shrink-0" />{doc.title}
    </a>
  );
}

/**
 * A payslip's `⋯` menu: « Voir » (a PDF or an image), « Télécharger », « Voir le document », then, for an actor who writes the source, the document
 * and every link it would lose, « Remplacer » (a new upload with `replaceWith`) and « Retirer » (unlinks `links` only).
 */
export function PayslipMenu({ doc, links, replaceWith, writable, writes }: {
  doc: DocumentSummary; links: DocumentLink[]; replaceWith: AttachFields | null; writable: boolean; writes: PayslipWrites;
}) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const input = useRef<HTMLInputElement>(null);
  const [asking, setAsking] = useState(false);
  const preview = usePreview();
  const editable = writable && doc.canEdit && links.length > 0 && links.every((l) => l.canWrite);
  const replace = editable && replaceWith;
  const remove = t('income_detail.payslips.remove');
  return (
    <>
      <RowMenu label={t('income_detail.payslips.menu', { title: doc.title })} items={[
        ...(previewable(doc.mediaType) ? [{ label: t('documents_item.preview'), onSelect: () => preview.show(doc) }] : []),
        { label: t('income_detail.payslips.download'), href: contentUrl(doc.id) },
        { label: t('income_detail.payslips.open'), onSelect: () => void navigate({ to: '/documents/$id', params: { id: doc.id } }) },
        ...(replace ? [{ label: t('income_detail.payslips.replace'), onSelect: () => input.current?.click() }] : []),
        ...(editable ? [{ label: remove, onSelect: () => setAsking(true), destructive: true }] : []),
      ]} />
      {preview.dialog}
      {replace ? (
        <FileInput ref={input} label={t('income_detail.payslips.replace')}
          onFile={(file) => void writes.replace(doc, links, file, replace.target, replace.date, replace.title)} />
      ) : null}
      {asking ? (
        <Suspense fallback={null}>
          <ConfirmDialog label={remove} confirmation={t('income_detail.payslips.remove_confirm')} pending={writes.pending}
            onClose={() => setAsking(false)} onConfirm={() => { setAsking(false); void writes.remove(doc, links); }} />
        </Suspense>
      ) : null}
    </>
  );
}

/** A refused upload reads as on the documents page (by the file's name); any other failure as a problem. */
export function PayslipProblem({ writes }: { writes: PayslipWrites }) {
  const { t } = useTranslation();
  if (!writes.problem) return null;
  return writes.file !== null
    ? <p role="alert" className="text-sm text-destructive">{t(`documents_page.upload.${refusal(writes.problem)}`, { name: writes.file })}</p>
    : <ProblemMessage problem={present(writes.problem)} />;
}

/** Opened by a button or a menu entry; out of the page's tab order and accessibility tree. */
export function FileInput({ ref, label, onFile }: { ref: Ref<HTMLInputElement>; label: string; onFile: (file: File) => void }) {
  return (
    <input ref={ref} type="file" accept={ACCEPT} hidden aria-label={label} onChange={(event) => {
      const file = event.currentTarget.files?.[0];
      event.currentTarget.value = ''; // picking the same file again still sends it
      if (file) onFile(file);
    }} />
  );
}
