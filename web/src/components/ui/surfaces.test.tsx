import { render, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it } from 'vitest';
import { AlertDialog, AlertDialogContent, AlertDialogDescription, AlertDialogTitle } from './alert-dialog';
import { Segmented } from '../form/Segmented';
import { buttonVariants } from './button';
import { Card } from './card';
import { CommandDialog, CommandInput } from './command';
import { Dialog, DialogContent, DialogDescription, DialogTitle } from './dialog';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSub, DropdownMenuSubContent, DropdownMenuSubTrigger, DropdownMenuTrigger } from './dropdown-menu';
import { Popover, PopoverContent, PopoverTrigger } from './popover';
import { Select, SelectContent, SelectItem, SelectTrigger } from './select';
import { Sheet, SheetContent, SheetDescription, SheetTitle } from './sheet';

// Radix portals the content to the body and makes the rest inert: queries use hidden: true.
const overlays: [string, string, ReactNode][] = [
  ['Dialog', 'dialog', <Dialog open><DialogContent><DialogTitle>t</DialogTitle><DialogDescription>d</DialogDescription></DialogContent></Dialog>],
  ['AlertDialog', 'alertdialog', <AlertDialog open><AlertDialogContent><AlertDialogTitle>t</AlertDialogTitle><AlertDialogDescription>d</AlertDialogDescription></AlertDialogContent></AlertDialog>],
  ['Popover', 'dialog', <Popover open><PopoverTrigger>o</PopoverTrigger><PopoverContent>p</PopoverContent></Popover>],
  ['DropdownMenu', 'menu', <DropdownMenu open><DropdownMenuTrigger>o</DropdownMenuTrigger><DropdownMenuContent><DropdownMenuItem>i</DropdownMenuItem></DropdownMenuContent></DropdownMenu>],
  ['Select', 'listbox', <Select open><SelectTrigger>o</SelectTrigger><SelectContent><SelectItem value="a">a</SelectItem></SelectContent></Select>],
  ['Sheet', 'dialog', <Sheet open><SheetContent><SheetTitle>t</SheetTitle><SheetDescription>d</SheetDescription></SheetContent></Sheet>],
];

describe('surfaces', () => {
  it('a card is frosted material, not glass', () => {
    render(<Card data-testid="card" />);
    const card = screen.getByTestId('card');
    expect(card).toHaveClass('material');
    expect(card).not.toHaveClass('glass');
    expect(card).not.toHaveClass('bg-card');
  });

  it.each(overlays)('%s content is regular glass', (_name, role, ui) => {
    render(ui);
    const content = screen.getByRole(role, { hidden: true });
    expect(content).toHaveClass('glass-regular');
    expect(content).not.toHaveClass('bg-popover');
    expect(content).not.toHaveClass('bg-background');
  });

  // The class merged on the open menu: an own `bg-…` would paint over the dialog's glass.
  it('the command list sits transparent in its open overlay', () => {
    render(<CommandDialog open title="t" description="d"><CommandInput /></CommandDialog>);
    const dialog = screen.getByRole('dialog', { hidden: true });
    expect(dialog).toHaveClass('glass-regular');
    const command = dialog.querySelector('[data-slot="command"]');
    expect(command).toHaveClass('bg-transparent');
    expect(command?.className).not.toMatch(/\bbg-(popover|background|card)\b/);
  });

  it('an open submenu is regular glass', () => {
    render(
      <DropdownMenu open>
        <DropdownMenuTrigger>o</DropdownMenuTrigger>
        <DropdownMenuContent>
          <DropdownMenuSub open>
            <DropdownMenuSubTrigger>s</DropdownMenuSubTrigger>
            <DropdownMenuSubContent><DropdownMenuItem>i</DropdownMenuItem></DropdownMenuSubContent>
          </DropdownMenuSub>
        </DropdownMenuContent>
      </DropdownMenu>,
    );
    const sub = document.querySelector('[data-slot="dropdown-menu-sub-content"]');
    expect(sub).toHaveAttribute('data-state', 'open');
    expect(sub).toHaveClass('glass-regular');
    expect(sub).not.toHaveClass('bg-popover');
  });

  it.each(['default', 'sm', 'lg', 'icon'] as const)('a %s button is a capsule', (size) => {
    const classes = buttonVariants({ size });
    expect(classes).toContain('rounded-full');
    expect(classes).not.toContain('rounded-md');
  });

  // The track's radius is half a row's height: a capsule on one row, a rounded rectangle once its options wrap.
  it('a segmented control is a capsule', () => {
    render(<Segmented value="a" onValueChange={() => {}} aria-label="s" options={[{ value: 'a', label: 'A' }, { value: 'b', label: 'B' }]} />);
    expect(screen.getByRole('radiogroup')).toHaveClass('rounded-[1.125rem]');
    expect(screen.getByRole('radio', { name: 'A' })).toHaveClass('rounded-full');
  });
});
