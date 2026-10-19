import { Link } from '@tanstack/react-router';
import { ChevronsUpDown } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { visibleNav } from './nav';
import { useSession } from './Providers';

/** The account pages and « Se déconnecter », behind the signed-in person's name. */
export function PersonMenu() {
  const { t } = useTranslation();
  const { session, capabilities, signOut } = useSession();
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" className="w-full justify-between">
          <span className="truncate">{session.person.name}</span>
          <ChevronsUpDown aria-hidden="true" className="size-4 shrink-0" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="w-56">
        {visibleNav(capabilities).filter((item) => item.group === 'account').map((item) => (
          <DropdownMenuItem key={item.to} asChild>
            <Link to={item.to}><item.icon aria-hidden="true" />{t(item.label)}</Link>
          </DropdownMenuItem>
        ))}
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={() => void signOut()}>{t('shell.sign_out')}</DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
