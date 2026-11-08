import { useState } from 'react';
import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { Button } from '@/components/ui/button';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { visibleNav } from '@/components/nav';
import { useSession } from './Providers';
import { AVATAR, initials } from './sidebar';

const VERSION = import.meta.env.VITE_NID_VERSION ?? 'dev';

/**
 * The signed-in person's menu, at the end of the top bar (screens.md §1): their name, the account pages, « Se déconnecter »,
 * then the version; opening it asks the API for its own, a click on the version opens « Nouveautés » (`/changelog`).
 */
export function PersonMenu() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session, capabilities, signOut } = useSession();
  const [open, setOpen] = useState(false);
  const api = $api.useQuery('get', '/api/v1/platform/version', {}, { enabled: open, staleTime: Infinity });
  const name = session.person.name;
  return (
    <DropdownMenu open={open} onOpenChange={setOpen}>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="icon" aria-label={name} className="rounded-full">
          <span aria-hidden="true" className={AVATAR}>{initials(name)}</span>
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-56">
        <DropdownMenuLabel className="truncate">{name}</DropdownMenuLabel>
        <DropdownMenuSeparator />
        {visibleNav(capabilities).filter((item) => item.group === 'account').map((item) => (
          <DropdownMenuItem key={item.to} asChild>
            <Link to={item.to}><item.icon aria-hidden="true" />{t(item.label)}</Link>
          </DropdownMenuItem>
        ))}
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={() => void signOut()}>{t('shell.sign_out')}</DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuItem asChild className="justify-between text-xs text-muted-foreground">
          <Link to="/changelog">
            <span>{t('app.version', { version: VERSION })}</span>
            <span>{t('app.api_version', { version: api.data?.version ?? (api.isError ? t('app.api_version_unknown') : '…') })}</span>
          </Link>
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
