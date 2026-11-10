import { useNavigate, useSearch } from '@tanstack/react-router';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';

export const PAGE_SIZE = 50;

/** Previous / next with « Page x sur y »; nothing when everything fits on one page. `label` names the nav when a screen has several. */
export function Pager({ page, total, pageSize = PAGE_SIZE, onPage, label }: {
  page: number; total: number; pageSize?: number; onPage: (n: number) => void; label?: string;
}) {
  const { t } = useTranslation();
  const pages = Math.max(1, Math.ceil(total / pageSize));
  if (pages <= 1) return null;
  return (
    <nav aria-label={label ?? t('pager.pages')} className="flex items-center justify-between gap-3">
      <Button type="button" variant="outline" size="sm" disabled={page <= 1} onClick={() => onPage(page - 1)}>{t('pager.previous')}</Button>
      <span className="text-sm text-muted-foreground">{t('pager.page_of', { page, pages })}</span>
      <Button type="button" variant="outline" size="sm" disabled={page >= pages} onClick={() => onPage(page + 1)}>{t('pager.next')}</Button>
    </nav>
  );
}

/** The `page` of the URL (absent is 1, so it stays out of the URL there), and how to change it. */
export function useListPage(): [page: number, toPage: (n: number) => void] {
  const { page }: { page?: number } = useSearch({ strict: false });
  const navigate = useNavigate();
  const toPage = (n: number) => void navigate({ to: '.', replace: true, resetScroll: false,
    search: ((prev: Record<string, unknown>) => ({ ...prev, page: n > 1 ? n : undefined })) as never });
  return [page ?? 1, toPage];
}

/** A page past the end (the last rows went away) steps back to the last one. */
export function useStepBack(page: number, answer: { items: unknown[]; total: number } | undefined, toPage: (n: number) => void) {
  useEffect(() => {
    if (answer && answer.items.length === 0 && page > 1 && answer.total > 0) toPage(Math.ceil(answer.total / PAGE_SIZE));
  });
}
