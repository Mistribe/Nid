import { Link } from '@tanstack/react-router';
import { ChevronsUpDown } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { visibleNav } from './nav';
import { useSession } from './Providers';
import { initials } from './sidebar';

/** The account pages and « Se déconnecter », behind the signed-in person's name (their initials on the rail). */
export function PersonMenu({ rail = false }: { rail?: boolean }) {
  const { t } = useTranslation();
  const { session, capabilities, signOut } = useSession();
  const name = session.person.name;
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        {rail ? (
          <Button variant="ghost" size="icon" aria-label={name} className="mx-auto flex size-10 rounded-full">
            <span aria-hidden="true" className="flex size-8 items-center justify-center rounded-full bg-primary/15 text-xs font-semibold text-foreground">{initials(name)}</span>
          </Button>
        ) : (
          <Button variant="ghost" className="w-full justify-between">
            <span className="truncate">{name}</span>
            <ChevronsUpDown aria-hidden="true" className="size-4 shrink-0" />
          </Button>
        )}
      </DropdownMenuTrigger>
      <DropdownMenuContent align={rail ? 'end' : 'start'} side={rail ? 'right' : 'bottom'} sideOffset={rail ? 16 : 4} className="w-56">
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
