import { useQueryClient } from '@tanstack/react-query';
import { UploadIcon } from 'lucide-react';
import { useRef, useState, type DragEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { toProblem, type Problem } from '@/api/errors';
import type { FieldControl } from '@/components/form/Field';
import { buttonVariants } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { invalidateDocuments, uploadDocument } from './api';
import '@/locales/documents';

type Upload = { key: number; name: string; state: 'sending' | 'done' | Problem };

export const ACCEPT = '.pdf,.png,.jpg,.jpeg,.eml,application/pdf,image/png,image/jpeg,message/rfc822';

/** Each file goes alone, one after the other (the API spills each form to a size-limited tmpfs): one refused file shows its own error and does not stop the others. */
export function useUploads() {
  const { fetchClient } = useApi();
  const queryClient = useQueryClient();
  const [uploads, setUploads] = useState<Upload[]>([]);
  const next = useRef(0);
  const queue = useRef<Promise<void>>(Promise.resolve());
  const settle = (key: number, state: Upload['state']) => setUploads((list) => list.map((u) => (u.key === key ? { ...u, state } : u)));
  const send = (files: File[]) => {
    for (const file of files) {
      const key = next.current++;
      setUploads((list) => [...list, { key, name: file.name, state: 'sending' }]);
      queue.current = queue.current.then(() => uploadDocument(fetchClient, file).then(
        () => { settle(key, 'done'); void invalidateDocuments(queryClient); },
        (error: unknown) => settle(key, toProblem(error)),
      ));
    }
  };
  const refuse = (file: File) => setUploads((list) => [...list, { key: next.current++, name: file.name, state: UNSUPPORTED }]);
  return { uploads, send, refuse };
}

/** « Déposer »: the file picker, several files at once. */
export function UploadPicker({ onFiles }: { onFiles: (files: File[]) => void }) {
  const { t } = useTranslation();
  return (
    <label className={cn(buttonVariants(), 'cursor-pointer focus-within:ring-[3px] focus-within:ring-ring/50')}>
      {t('documents_page.upload.pick')}
      <input type="file" multiple accept={ACCEPT} className="sr-only" onChange={(event) => {
        const files = [...(event.currentTarget.files ?? [])];
        event.currentTarget.value = ''; // picking the same file again still sends it
        onFiles(files);
      }} />
    </label>
  );
}

/** What the API answers for a file of a type it does not take: a file refused before it is sent reads the same. */
export const UNSUPPORTED: Problem = { status: 415, code: 'documents.unsupported_type' };

/** Whether an input's `accept` list (extensions and media types) takes the file, as the picker's own filter does. */
export function accepts(accept: string, file: File) {
  const name = file.name.toLowerCase();
  return accept.split(',').map((a) => a.trim().toLowerCase()).some((a) =>
    a.startsWith('.') ? name.endsWith(a) : a.endsWith('/*') ? file.type.startsWith(a.slice(0, -1)) : file.type === a);
}

type Take = { onFiles: (files: [File, ...File[]]) => void; onRefused?: (file: File) => void; accept?: string; multiple?: boolean; disabled?: boolean };

/** Dropped or picked files sorted by `accept` when there is `onRefused` (only the first unless `multiple`), and the drag handlers that take them on any element. */
export function useDropFiles({ onFiles, onRefused, accept = ACCEPT, multiple = true, disabled = false }: Take) {
  const [over, setOver] = useState(false);
  const take = (files: File[]) => {
    const kept = multiple ? files : files.slice(0, 1);
    // Without `onRefused` every file goes on, for the caller to word what the API refuses.
    const refused = onRefused ? kept.filter((file) => !accepts(accept, file)) : [];
    for (const file of refused) onRefused?.(file);
    const [first, ...rest] = kept.filter((file) => !refused.includes(file));
    if (first) onFiles([first, ...rest]);
  };
  const handlers = {
    'data-drop-zone': '',
    onDragOver: (event: DragEvent) => { event.preventDefault(); if (!disabled) setOver(true); },
    onDragLeave: () => setOver(false),
    onDrop: (event: DragEvent) => { event.preventDefault(); setOver(false); if (!disabled) take([...event.dataTransfer.files]); },
  };
  return { over, take, handlers };
}

/**
 * The drop zone, the one way to add a file. With `pick` it holds its own picker (its keyboard way), which takes the
 * `id` and `aria-describedby` a `Field` hands it; without, a picker beside it (`UploadPicker`) does. `picked` names the file taken.
 */
export function UploadZone({ hint, pick, picked, id, className, 'aria-describedby': describedBy, 'aria-invalid': invalid, ...take }: Take & Partial<FieldControl> & {
  hint?: string; pick?: string; picked?: string | null; className?: string;
}) {
  const { t } = useTranslation();
  const { over, take: send, handlers } = useDropFiles(take);
  return (
    <div {...handlers}
      className={cn('flex flex-col items-center gap-1 rounded-lg border-2 border-dashed p-4 text-center transition-colors', over && 'border-primary bg-accent', className)}>
      <UploadIcon aria-hidden="true" className="size-6 text-muted-foreground" />
      <p>{t(take.multiple === false ? 'documents_page.upload.drop_one' : 'documents_page.upload.drop')}</p>
      <p className="text-sm text-muted-foreground">{hint ?? t('documents_page.upload.hint')}</p>
      {pick ? (
        <label className={cn(buttonVariants({ variant: 'outline', size: 'sm' }), 'mt-2 cursor-pointer focus-within:ring-[3px] focus-within:ring-ring/50', take.disabled && 'pointer-events-none opacity-50')}>
          {pick}
          <input id={id} aria-describedby={describedBy} aria-invalid={invalid} type="file" multiple={take.multiple !== false} accept={take.accept ?? ACCEPT} disabled={take.disabled} className="sr-only" onChange={(event) => {
            const files = [...(event.currentTarget.files ?? [])];
            event.currentTarget.value = ''; // picking the same file again still takes it
            send(files);
          }} />
        </label>
      ) : null}
      {picked ? <p className="mt-1 text-sm font-medium break-all">{picked}</p> : null}
    </div>
  );
}

export function refusal(problem: Problem): 'too_large' | 'unsupported_type' | 'failed' {
  if (problem.code === 'documents.too_large' || problem.status === 413) return 'too_large';
  if (problem.code === 'documents.unsupported_type' || problem.status === 415) return 'unsupported_type';
  return 'failed';
}

/** One line per file of this visit: sending, done, or why it was refused. */
export function UploadList({ uploads }: { uploads: Upload[] }) {
  const { t } = useTranslation();
  // Always on the page, empty at first: a live region added with its first message is not announced.
  return (
    <ul aria-label={t('documents_page.upload.progress')} aria-live="polite" className="space-y-1 text-sm">
      {uploads.map((u) => typeof u.state === 'object' ? (
        <li key={u.key} className="text-destructive">{t(`documents_page.upload.${refusal(u.state)}`, { name: u.name })}</li>
      ) : (
        <li key={u.key} className="flex items-center justify-between gap-2">
          <span className="truncate">{u.name}</span>
          <span className={u.state === 'done' ? 'text-status-good-text' : 'text-muted-foreground'}>
            {t(u.state === 'done' ? 'documents_page.upload.done' : 'documents_page.upload.sending')}
          </span>
        </li>
      ))}
    </ul>
  );
}
