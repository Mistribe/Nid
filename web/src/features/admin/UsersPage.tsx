import { useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import type { components } from '@/api/schema';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { AdminFrame, AdminSkeleton } from './AdminFrame';
import { ConfirmAction } from './ConfirmAction';

type Adult = components['schemas']['AdultView'];

const USERS = '/api/v1/admin/users';
const HOUSEHOLD = '/api/v1/admin/household';

/** screens.md §12, Utilisateurs: who may join the household, and who administers it. */
export function UsersPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<Presentation | null>(null);
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
  const add = $api.useMutation('post', USERS, settle);
  const remove = $api.useMutation('delete', '/api/v1/admin/users/{id}', settle);
  const setAdmin = $api.useMutation('put', '/api/v1/admin/users/{id}/admin', settle);
  const revoke = $api.useMutation('post', '/api/v1/admin/users/{id}/revoke-sessions', settle);

  const title = t('admin.users.title');
  const loadError = adults.error ?? household.error;
  if (loadError) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(loadError))} /></AdminFrame>;
  if (!adults.data || !household.data) return <AdminFrame title={title}><AdminSkeleton /></AdminFrame>;

  const activeAdmins = adults.data.filter((adult) => adult.isAdmin && adult.status === 'active').length;
  const capReached = household.data.adultCount >= household.data.maxAdults;

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const form = event.currentTarget;
    const fields = new FormData(form);
    add.mutate(
      {
        body: {
          displayName: (fields.get('displayName') as string | null) ?? '',
          email: (fields.get('email') as string | null) ?? '',
        },
      },
      { onSuccess: () => form.reset() },
    );
  };

  const row = (adult: Adult) => {
    const isLastAdmin = adult.isAdmin && adult.status === 'active' && activeAdmins === 1;
    const target = { params: { path: { id: adult.id } } };
    return (
      <tr key={adult.id} className="border-b align-top">
        <td className="py-2 pr-4">{adult.displayName}</td>
        <td className="py-2 pr-4">{adult.email}</td>
        <td className="py-2 pr-4">{t(adult.status === 'pending' ? 'admin.users.pending' : 'admin.users.active')}</td>
        <td className="py-2 pr-4">{adult.isAdmin ? t('admin.users.admin') : null}</td>
        <td className="flex flex-col items-start gap-2 py-2">
          {adult.isAdmin ? (
            <>
              <ConfirmAction
                label={t('admin.users.demote')}
                confirmation={t('admin.users.demote_confirm', { name: adult.displayName })}
                disabled={isLastAdmin}
                onConfirm={() => setAdmin.mutate({ ...target, body: { isAdmin: false } })}
              />
              {isLastAdmin ? <p className="text-sm text-muted-foreground">{t('admin.users.last_admin')}</p> : null}
            </>
          ) : (
            <ConfirmAction
              label={t('admin.users.promote')}
              confirmation={t('admin.users.promote_confirm', { name: adult.displayName })}
              onConfirm={() => setAdmin.mutate({ ...target, body: { isAdmin: true } })}
            />
          )}
          {adult.status === 'pending' ? (
            <ConfirmAction
              label={t('admin.users.cancel_invitation')}
              confirmation={t('admin.users.cancel_invitation_confirm', { email: adult.email ?? '' })}
              onConfirm={() => remove.mutate(target)}
            />
          ) : (
            <ConfirmAction
              label={t('admin.users.revoke')}
              confirmation={t('admin.users.revoke_confirm', { name: adult.displayName })}
              onConfirm={() => revoke.mutate(target)}
            />
          )}
        </td>
      </tr>
    );
  };

  return (
    <AdminFrame title={title}>
      {problem ? <ProblemMessage problem={problem} /> : null}
      <table className="w-full text-left">
        <thead>
          <tr className="border-b">
            <th scope="col" className="py-2 pr-4">{t('admin.users.name')}</th>
            <th scope="col" className="py-2 pr-4">{t('admin.users.email')}</th>
            <th scope="col" className="py-2 pr-4">{t('admin.users.status')}</th>
            <th scope="col" className="py-2 pr-4">{t('admin.users.role')}</th>
            <th scope="col" className="py-2">{t('admin.users.actions')}</th>
          </tr>
        </thead>
        <tbody>{adults.data.map(row)}</tbody>
      </table>

      <form onSubmit={submit} className="max-w-md space-y-3">
        <h2 className="text-lg font-semibold">{t('admin.users.add_title')}</h2>
        <p className="text-sm text-muted-foreground">{t('admin.users.add_hint')}</p>
        <fieldset disabled={capReached || add.isPending} className="space-y-3">
          <label className="block">
            <span className="block text-sm">{t('admin.users.name')}</span>
            <input name="displayName" required autoComplete="off" className="w-full rounded-md border px-3 py-2" />
          </label>
          <label className="block">
            <span className="block text-sm">{t('admin.users.email')}</span>
            <input name="email" type="email" required autoComplete="off" className="w-full rounded-md border px-3 py-2" />
          </label>
          <Button type="submit">{t('admin.users.add')}</Button>
        </fieldset>
        {capReached ? <p className="text-sm text-muted-foreground">{t('admin.users.cap_reached', { max: household.data.maxAdults })}</p> : null}
      </form>
    </AdminFrame>
  );
}
