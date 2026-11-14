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
import { ModalDialog } from '@/components/ModalDialog';
import { formatInstant } from '@/lib/format';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { elision } from '@/lib/elision';
import { AdminFrame } from './AdminFrame';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
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
  const { session, reload } = useSession();
  const [adding, setAdding] = useState(false);
  const [capNotice, setCapNotice] = useState(false);
  const [problem, setProblem] = useState<Presentation | null>(null);
  const [asking, setAsking] = useState<Adult | null>(null);
  // Issue 215: a one-time password link just created, shown once.
  const [link, setLink] = useState<{ adult: Adult; url: string; purpose: string; expiresAt: string } | null>(null);
  const local = session.methods.includes('password');
  // Issue 215: who decides the admin role (local-accounts.md §6); the admin area already reads the system status.
  const nidDecidesAdmin = $api.useQuery('get', '/api/v1/admin/system').data?.auth.adminSource === 'nid';
  const [roleAsk, setRoleAsk] = useState<Adult | null>(null);
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
  const createLink = $api.useMutation('post', '/api/v1/admin/users/{id}/password-link', settle);
  const unlock = $api.useMutation('post', '/api/v1/admin/users/{id}/unlock', {
    ...settle,
    onSuccess: () => { settle.onSuccess(); toast.success(t('admin.users.unlocked')); },
  });
  const setRole = $api.useMutation('put', '/api/v1/admin/users/{id}/admin', settle);

  const title = t('admin.users.title');
  const loadError = adults.error ?? household.error;
  if (loadError) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(loadError))} /></AdminFrame>;
  if (!adults.data || !household.data) return <AdminFrame title={title}><Loading label={t('admin.loading')}><TableSkeleton columns={5} /></Loading></AdminFrame>;

  const capReached = household.data.adultCount >= household.data.maxAdults;

  const row = (adult: Adult) => {
    const pending = adult.status === 'pending';
    const hasPassword = adult.methods.includes('password');
    const localItems = local ? [
      {
        label: t(hasPassword ? 'admin.users.reset_link' : 'admin.users.enroll_link'),
        onSelect: () => createLink.mutate({ params: { path: { id: adult.id } }, body: { revokeTokens: false } }, {
          onSuccess: (created) => setLink({ adult, url: created.url, purpose: created.purpose, expiresAt: created.expiresAt }),
        }),
      },
      ...(adult.lockedUntil ? [{ label: t('admin.users.unlock'), onSelect: () => unlock.mutate({ params: { path: { id: adult.id } } }) }] : []),
    ] : [];
    const roleItems = nidDecidesAdmin
      ? [{ label: t(adult.isAdmin ? 'admin.users.demote' : 'admin.users.promote'), onSelect: () => setRoleAsk(adult) }]
      : [];
    return (
      <TableRow key={adult.id}>
        <TableCell>{adult.displayName}</TableCell>
        <TableCell>{adult.email}</TableCell>
        <TableCell>
          <span className="flex flex-wrap gap-1">
            <Badge variant={pending ? 'outline' : 'secondary'}>{t(pending ? 'admin.users.pending' : 'admin.users.active')}</Badge>
            {adult.lockedUntil ? <Badge variant="outline">{t('admin.users.locked')}</Badge> : null}
            {adult.linkExpiresAt ? <Badge variant="outline">{t('admin.users.link_pending')}</Badge> : null}
          </span>
        </TableCell>
        <TableCell>{adult.isAdmin ? <Badge variant="secondary">{t('admin.users.admin')}</Badge> : null}</TableCell>
        <TableCell className="text-right">
          <RowMenu label={t('admin.users.menu', { name: adult.displayName, context: elision(adult.displayName) })} triggerRef={(node) => { menus.current[adult.id] = node; }}
            items={[...localItems, ...roleItems, { label: t(pending ? 'admin.users.cancel_invitation' : 'admin.users.revoke'), destructive: pending, onSelect: () => setAsking(adult) }]} />
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
          <Link to="/admin/household" className="font-medium underline underline-offset-4">{t('nav.admin_household')}</Link>
        </p>
      ) : null}
      {adding ? <AddAdultDialog emailOptional={local && !session.methods.includes('oidc')} onClose={() => setAdding(false)} onDone={(message) => { setAdding(false); toast.success(message); }} /> : null}
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
      {roleAsk ? (
        <ConfirmFromMenu label={t(roleAsk.isAdmin ? 'admin.users.demote' : 'admin.users.promote')}
          confirmation={roleAsk.isAdmin
            ? t(roleAsk.id === session.person.id ? 'admin.users.demote_self_confirm' : 'admin.users.demote_confirm', { name: roleAsk.displayName })
            : t('admin.users.promote_confirm', { name: roleAsk.displayName })}
          pending={setRole.isPending} returnFocus={{ current: menus.current[roleAsk.id] ?? null }}
          onClose={() => setRoleAsk(null)}
          onConfirm={() => setRole.mutate({ params: { path: { id: roleAsk.id } }, body: { admin: !roleAsk.isAdmin } }, {
            // Without the role, the admin area is no longer theirs: the app reloads its capabilities.
            onSuccess: () => { if (roleAsk.isAdmin && roleAsk.id === session.person.id) reload(); },
          })} />
      ) : null}
      {link ? <PasswordLinkDialog {...link} onClose={() => setLink(null)} /> : null}
      <p className="max-w-prose text-sm text-muted-foreground">{t(local ? 'admin.users.managed_hint_local' : 'admin.users.managed_hint')}{nidDecidesAdmin ? <> {t('admin.users.role_hint_nid')}</> : null}</p>
    </AdminFrame>
  );
}

/** « Ajouter un adulte »: the pre-registration, in a dialog; the page toasts the outcome. */
function AddAdultDialog({ emailOptional, onClose, onDone }: { emailOptional: boolean; onClose: () => void; onDone: (message: string) => void }) {
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
          <p className="text-sm text-muted-foreground">{t(emailOptional ? 'admin.users.add_hint_local' : 'admin.users.add_hint')}</p>
          {problem ? <ProblemMessage problem={problem} /> : null}
          <Field label={t('admin.users.name')}>{(c) => <Input {...c} name="displayName" required autoComplete="off" />}</Field>
          <Field label={t('admin.users.email')}>{(c) => <Input {...c} name="email" type="email" required={!emailOptional} autoComplete="off" />}</Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={add.isPending}>{t('admin.users.add')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

/** Issue 215 (local-accounts.md §7): the link, once; the admin passes it on by the means of their choice. */
function PasswordLinkDialog({ adult, url, purpose, expiresAt, onClose }: { adult: Adult; url: string; purpose: string; expiresAt: string; onClose: () => void }) {
  const { t } = useTranslation();
  return (
    <ModalDialog title={t('admin.users.link_title')} onClose={onClose}>
      {() => (
        <div className="space-y-3">
          <p>{t(purpose === 'reset' ? 'admin.users.link_reset_body' : 'admin.users.link_enroll_body', { name: adult.displayName })}</p>
          <Input readOnly value={url} aria-label={t('admin.users.link_title')} onFocus={(event) => event.currentTarget.select()} className="font-mono text-xs" />
          <p className="text-sm text-muted-foreground">{t('admin.users.link_expires', { at: formatInstant(expiresAt) })}</p>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => {
              void navigator.clipboard?.writeText(url).then(() => toast.success(t('admin.users.copied')), () => {});
            }}>{t('admin.users.copy')}</Button>
            <Button type="button" onClick={onClose}>{t('admin.users.close')}</Button>
          </div>
        </div>
      )}
    </ModalDialog>
  );
}
