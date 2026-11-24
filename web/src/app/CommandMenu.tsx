import { useNavigate } from '@tanstack/react-router';
import { CalendarCheck, ChartLine, FileText, Plus, Search } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ModuleTile } from '@/components/ModuleTile';
import { CommandDialog, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from '@/components/ui/command';
import { visibleNav } from '@/components/nav';
import { SEARCH_MIN_LENGTH, useDocumentSearch } from '@/features/documents/api';
import { Match } from '@/features/documents/Match';
import { useDebounced } from '@/lib/useDebounced';
import { useSession } from './Providers';
import '@/locales/palette';

/** The pages outside the navigation, each only when its module is enabled. */
const PAGES = [{ module: 'core', to: '/reports/year', label: 'palette.year_review' }] as const;

/** The creation pages, each only when its module is enabled (as the dashboard's empty state). */
const CREATE = [
  { module: 'investments', to: '/investments/new', label: 'palette.new_envelope' },
  { module: 'realestate', to: '/properties/new', label: 'palette.new_property' },
  { module: 'realestate', to: '/loans/new', label: 'palette.new_loan' },
  { module: 'subscriptions', to: '/subscriptions/new', label: 'palette.new_subscription' },
  { module: 'income', to: '/income/new', label: 'palette.new_income' },
  { module: 'goals', to: '/goals/new', label: 'palette.new_goal' },
  { module: 'childcare', to: '/childcare/new', label: 'palette.new_childcare' },
] as const;

/** #170: the documents the palette shows; « Tous les résultats » opens the Documents page on the same search. */
const SHOWN_DOCUMENTS = 5;

/**
 * ⌘K / Ctrl K: every page the person can reach and the creation pages, filtered as they type. Loaded on its first opening
 * (cmdk stays out of the initial bundle); the router keeps Foyer / Moi. #170: with the Documents module, a « Documents » group
 * with the Documents page's search (#169) over every document the person reads, after a pause in the typing.
 */
export function CommandMenu({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useTranslation();
  const { capabilities } = useSession();
  const navigate = useNavigate();
  const go = (to: string) => {
    onOpenChange(false);
    void navigate({ to, search: (prev: { scope?: 'me' }) => ({ scope: prev.scope }) });
  };
  const [typed, setTyped] = useState('');
  const search = useDebounced(typed.trim(), 300);
  const withDocuments = capabilities.modules.includes('documents');
  const documents = useDocumentSearch(withDocuments ? search : '');
  const found = search.length >= SEARCH_MIN_LENGTH ? documents.data : undefined;
  const openDocuments = () => {
    onOpenChange(false);
    void navigate({ to: '/documents', search: { scope: 'me', q: search } as never });
  };
  return (
    <CommandDialog open={open} onOpenChange={onOpenChange} title={t('palette.title')} description={t('palette.placeholder')}>
      <CommandInput placeholder={t('palette.placeholder')} value={typed} onValueChange={setTyped} />
      <CommandList>
        <CommandEmpty>{t('palette.empty')}</CommandEmpty>
        <CommandGroup heading={t('palette.go')}>
          {visibleNav(capabilities).map((item) => (
            <CommandItem key={item.to} value={t(item.label)} keywords={item.group === 'admin' ? [t('nav.admin')] : undefined} onSelect={() => go(item.to)}>
              <ModuleTile icon={item.icon} tile={item.tile} />
              {t(item.label)}
            </CommandItem>
          ))}
          <CommandItem value={t('palette.series')} onSelect={() => go('/series')}>
            <ModuleTile icon={ChartLine} tile="primary" />
            {t('palette.series')}
          </CommandItem>
          {PAGES.filter((p) => capabilities.modules.includes(p.module)).map((p) => (
            <CommandItem key={p.to} value={t(p.label)} onSelect={() => go(p.to)}>
              <ModuleTile icon={CalendarCheck} tile="primary" />
              {t(p.label)}
            </CommandItem>
          ))}
        </CommandGroup>
        {found && found.total > 0 ? (
          // The server already searched: cmdk's own filter must not hide its answers.
          <CommandGroup heading={t('palette.documents')} forceMount>
            {found.items.slice(0, SHOWN_DOCUMENTS).map((d) => (
              <CommandItem key={d.id} value={`document ${d.id}`} forceMount onSelect={() => go(`/documents/${d.id}`)} className="items-start">
                <FileText aria-hidden="true" className="mt-0.5" />
                <span className="min-w-0">
                  <span className="block">{d.title}</span>
                  {d.match ? <Match match={d.match} /> : null}
                </span>
              </CommandItem>
            ))}
            <CommandItem value="documents all results" forceMount onSelect={openDocuments}>
              <Search aria-hidden="true" />
              {t('palette.documents_all', { count: found.total })}
            </CommandItem>
          </CommandGroup>
        ) : null}
        <CommandGroup heading={t('palette.create')}>
          {CREATE.filter((c) => capabilities.modules.includes(c.module)).map((c) => (
            <CommandItem key={c.to} value={t(c.label)} onSelect={() => go(c.to)}>
              <Plus aria-hidden="true" />
              {t(c.label)}
            </CommandItem>
          ))}
        </CommandGroup>
      </CommandList>
    </CommandDialog>
  );
}
