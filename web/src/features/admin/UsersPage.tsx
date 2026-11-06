import { useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import type { components } from '@/api/schema';
import { useSession } from '@/app/Providers';
import { ProblemMessage } from '@/components/ProblemMessage';
import { RowMenu } from '@/components/page/RowMenu';
import { Field } from '@/components/form/Field';
import { FormDialog } from '@/components/FormDialog';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { elision } from '@/lib/elision';
import { AdminFrame, AdminSkeleton } from './AdminFrame';
import { ConfirmFromMenu } from '@/components/ConfirmAction';
import '@/locales/admin';

type Adult = components['schemas']['AdultView'];

const USERS = '/api/v1/admin/users';
const HOUSEHOLD = '/api/v1/admin/household';

/** screens.md §12, Utilisateurs: the household's adults. Access and the admin role come from the identity provider's groups. */
export function UsersPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const { session } = useSession();
  const [adding, setAdding] = useState(false);
  const [capNotice, setCapNotice] = useState(false);
  const [problem, setProblem] = useState<Presentation | null>(null);
  const [asking, setAsking] = useState<Adult | null>(null);
  // Each row's ⋯, where the focus goes back when the confirmation closes.
  const menus = useRef<Record<string, HTMLButtonElement | null>>({});
  const adults = $api.useQuery('get', USERS);
  const household = $api.useQuery('get', HOUSEHOLD);

  const settle = {
    onSuccess: () => {
      setProblem(null);
      void queryClient.invalidateQueries({ queryKey: ['get', USERS] });
      void queryClient.invalidateQueries({ queryKey: ['get', HOUSEHOLD] });
    },
    onError: (error: unknown) => setProblem(present(toProblem(error))),
  };
  const remove = $api.useMutation('delete', '/api/v1/admin/users/{id}', settle);
  const revoke = $api.useMutation('post', '/api/v1/admin/users/{id}/revoke-sessions', settle);

  const title = t('admin.users.title');
  const loadError = adults.error ?? household.error;
  if (loadError) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(loadError))} /></AdminFrame>;
  if (!adults.data || !household.data) return <AdminFrame title={title}><AdminSkeleton /></AdminFrame>;

  const capReached = household.data.adultCount >= household.data.maxAdults;

  const row = (adult: Adult) => {
    const pending = adult.status === 'pending';
    return (
      <TableRow key={adult.id}>
        <TableCell>{adult.displayName}</TableCell>
        <TableCell>{adult.email}</TableCell>
        <TableCell><Badge variant={pending ? 'outline' : 'secondary'}>{t(pending ? 'admin.users.pending' : 'admin.users.active')}</Badge></TableCell>
        <TableCell>{adult.isAdmin ? <Badge variant="secondary">{t('admin.users.admin')}</Badge> : null}</TableCell>
        <TableCell className="text-right">
          <RowMenu label={t('admin.users.menu', { name: adult.displayName, context: elision(adult.displayName) })} triggerRef={(node) => { menus.current[adult.id] = node; }}
            items={[{ label: t(pending ? 'admin.users.cancel_invitation' : 'admin.users.revoke'), destructive: pending, onSelect: () => setAsking(adult) }]} />
        </TableCell>
      </TableRow>
    );
  };

  const confirmation = (adult: Adult) => {
    if (adult.status === 'pending') return t('admin.users.cancel_invitation_confirm', { email: adult.email ?? '' });
    return adult.id === session.person.id ? t('admin.users.revoke_self_confirm') : t('admin.users.revoke_confirm', { name: adult.displayName });
  };

  return (
    // Never a disabled button (design-system.md §6): at the cap, the click says why and where to raise it.
    <AdminFrame title={title} actions={<Button onClick={() => (capReached ? setCapNotice(true) : setAdding(true))}>{t('admin.users.add_title')}</Button>}>
      {problem ? <ProblemMessage problem={problem} /> : null}
      {capNotice && capReached ? (
        <p role="status" className="text-sm">
          {t('admin.users.cap_reached', { max: household.data.maxAdults })}{' '}
          <Link to="/admin/household" className="font-medium underline underline-offset-4">{t('admin.nav.household')}</Link>
        </p>
      ) : null}
      {adding ? <AddAdultDialog onClose={() => setAdding(false)} onDone={(message) => { setAdding(false); toast.success(message); }} /> : null}
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t('admin.users.name')}</TableHead>
            <TableHead>{t('admin.users.email')}</TableHead>
            <TableHead>{t('admin.users.status')}</TableHead>
            <TableHead>{t('admin.users.role')}</TableHead>
            <TableHead><span className="sr-only">{t('page.more_actions')}</span></TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>{adults.data.map(row)}</TableBody>
      </Table>
      {asking ? (
        <ConfirmFromMenu label={t(asking.status === 'pending' ? 'admin.users.cancel_invitation' : 'admin.users.revoke')} confirmation={confirmation(asking)}
          pending={asking.status === 'pending' ? remove.isPending : revoke.isPending} returnFocus={{ current: menus.current[asking.id] ?? null }}
          onClose={() => setAsking(null)}
          onConfirm={() => {
            const target = { params: { path: { id: asking.id } } };
            if (asking.status === 'pending') remove.mutate(target); else revoke.mutate(target);
          }} />
      ) : null}
      <p className="max-w-prose text-sm text-muted-foreground">{t('admin.users.managed_hint')}</p>
    </AdminFrame>
  );
}

/** « Ajouter un adulte »: the pre-registration, in a dialog; the page toasts the outcome. */
function AddAdultDialog({ onClose, onDone }: { onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const add = $api.useMutation('post', USERS);
  const problem = add.error ? present(toProblem(add.error)) : null;

  return (
    <FormDialog title={t('admin.users.add_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          const fields = new FormData(event.currentTarget);
          add.mutate({ body: { displayName: (fields.get('displayName') as string | null) ?? '', email: (fields.get('email') as string | null) ?? '' } }, {
            onSuccess: () => {
              void queryClient.invalidateQueries({ queryKey: ['get', USERS] });
              void queryClient.invalidateQueries({ queryKey: ['get', HOUSEHOLD] });
              done(t('admin.users.added'));
            },
          });
        }}>
          <p className="text-sm text-muted-foreground">{t('admin.users.add_hint')}</p>
          {problem ? <ProblemMessage problem={problem} /> : null}
          <Field label={t('admin.users.name')}>{(c) => <Input {...c} name="displayName" required autoComplete="off" />}</Field>
          <Field label={t('admin.users.email')}>{(c) => <Input {...c} name="email" type="email" required autoComplete="off" />}</Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={add.isPending}>{t('admin.users.add')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
