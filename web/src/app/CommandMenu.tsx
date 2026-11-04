import { useNavigate } from '@tanstack/react-router';
import { Plus } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { ModuleTile } from '@/components/ModuleTile';
import { CommandDialog, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from '@/components/ui/command';
import { visibleNav } from './nav';
import { useSession } from './Providers';
import '@/locales/palette';

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

/**
 * ⌘K / Ctrl K: every page the person can reach and the creation pages, filtered as they type. Loaded on its first opening
 * (cmdk stays out of the initial bundle); the router keeps Foyer / Moi.
 */
export function CommandMenu({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useTranslation();
  const { capabilities } = useSession();
  const navigate = useNavigate();
  const go = (to: string) => {
    onOpenChange(false);
    void navigate({ to, search: (prev: { scope?: 'me' }) => ({ scope: prev.scope }) });
  };
  return (
    <CommandDialog open={open} onOpenChange={onOpenChange} title={t('palette.title')} description={t('palette.placeholder')}>
      <CommandInput placeholder={t('palette.placeholder')} />
      <CommandList>
        <CommandEmpty>{t('palette.empty')}</CommandEmpty>
        <CommandGroup heading={t('palette.go')}>
          {visibleNav(capabilities).map((item) => (
            <CommandItem key={item.to} value={t(item.label)} onSelect={() => go(item.to)}>
              <ModuleTile icon={item.icon} tile={item.tile} />
              {t(item.label)}
            </CommandItem>
          ))}
        </CommandGroup>
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
