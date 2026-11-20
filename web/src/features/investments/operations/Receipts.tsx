import { PaperclipIcon } from 'lucide-react';
import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { BankTransaction } from '@/api/banking';
import { Button } from '@/components/ui/button';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import '@/locales/banks';

const Preview = lazy(() => import('./ReceiptPreview'));

/** A line's receipts (the documents the reader may read on it): a paperclip with their count, each opening in the preview; nothing without any. */
export function Receipts({ line }: { line: BankTransaction }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState<{ id: string; title: string } | null>(null);
  const docs = line.documents ?? [];
  if (docs.length === 0) return null;
  return (
    <>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button type="button" variant="ghost" size="sm" className="h-7 gap-1 px-1.5 text-muted-foreground" aria-label={t('banks.operations.receipts', { count: docs.length })}>
            <PaperclipIcon aria-hidden="true" className="size-4" /><span aria-hidden="true">{docs.length}</span>
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start">
          {docs.map((d) => <DropdownMenuItem key={d.id} onSelect={() => setOpen(d)}>{d.title}</DropdownMenuItem>)}
        </DropdownMenuContent>
      </DropdownMenu>
      {open ? <Suspense fallback={null}><Preview id={open.id} title={open.title} onClose={() => setOpen(null)} /></Suspense> : null}
    </>
  );
}
