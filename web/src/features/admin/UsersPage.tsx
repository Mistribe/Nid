import { useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import type { components } from '@/api/schema';
import { useSession } from '@/app/Providers';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { AdminFrame, AdminSkeleton } from './AdminFrame';
import { ConfirmAction } from '@/components/ConfirmAction';

type Adult = components['schemas']['AdultView'];

const USERS = '/api/v1/admin/users';
const HOUSEHOLD = '/api/v1/admin/household';

/** screens.md §12, Utilisateurs: the household's adults. Access and the admin role come from the identity provider's groups. */
export function UsersPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const { session } = useSession();
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
  const revoke = $api.useMutation('post', '/api/v1/admin/users/{id}/revoke-sessions', settle);

  const title = t('admin.users.title');
  const loadError = adults.error ?? household.error;
  if (loadError) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(loadError))} /></AdminFrame>;
  if (!adults.data || !household.data) return <AdminFrame title={title}><AdminSkeleton /></AdminFrame>;

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
    const isSelf = adult.id === session.person.id;
    const target = { params: { path: { id: adult.id } } };
    return (
      <tr key={adult.id} className="border-b align-top">
        <td className="py-2 pr-4">{adult.displayName}</td>
        <td className="py-2 pr-4">{adult.email}</td>
        <td className="py-2 pr-4">{t(adult.status === 'pending' ? 'admin.users.pending' : 'admin.users.active')}</td>
        <td className="py-2 pr-4">{adult.isAdmin ? t('admin.users.admin') : null}</td>
        <td className="flex flex-col items-start gap-2 py-2">
          {adult.status === 'pending' ? (
            <ConfirmAction
              label={t('admin.users.cancel_invitation')}
              confirmation={t('admin.users.cancel_invitation_confirm', { email: adult.email ?? '' })}
              pending={remove.isPending}
              onConfirm={() => remove.mutate(target)}
            />
          ) : (
            <ConfirmAction
              label={t('admin.users.revoke')}
              confirmation={isSelf ? t('admin.users.revoke_self_confirm') : t('admin.users.revoke_confirm', { name: adult.displayName })}
              pending={revoke.isPending}
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
      <div className="overflow-x-auto">
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
      </div>
      <p className="max-w-prose text-sm text-muted-foreground">{t('admin.users.managed_hint')}</p>

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
