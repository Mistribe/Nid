import { useQueryClient } from '@tanstack/react-query';
import { UploadIcon } from 'lucide-react';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { toProblem, type Problem } from '@/api/errors';
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
  return { uploads, send };
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

/** The drop zone; the picker in the header is its keyboard way. */
export function UploadZone({ onFiles, hint }: { onFiles: (files: File[]) => void; hint?: string }) {
  const { t } = useTranslation();
  const [over, setOver] = useState(false);
  return (
    <div data-drop-zone=""
      onDragOver={(event) => { event.preventDefault(); setOver(true); }}
      onDragLeave={() => setOver(false)}
      onDrop={(event) => { event.preventDefault(); setOver(false); onFiles([...event.dataTransfer.files]); }}
      className={cn('flex flex-col items-center gap-1 rounded-lg border-2 border-dashed p-4 text-center transition-colors', over && 'border-primary bg-accent')}>
      <UploadIcon aria-hidden="true" className="size-6 text-muted-foreground" />
      <p>{t('documents_page.upload.drop')}</p>
      <p className="text-sm text-muted-foreground">{hint ?? t('documents_page.upload.hint')}</p>
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
