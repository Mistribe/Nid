import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ConfirmAction } from '@/features/admin/ConfirmAction';

/** `DELETE /imports/{id}`: cancels the session and clears its staging, after a confirmation in place. */
export function CancelImport({ id, label, onCancelled, beforeCancel }: {
  id: string; label: string; onCancelled: () => void; beforeCancel?: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [problem, setProblem] = useState<Presentation | null>(null);
  const cancel = $api.useMutation('delete', '/api/v1/platform/imports/{id}', {
    onSuccess: onCancelled,
    onError: (error) => setProblem(present(toProblem(error))),
  });
  return (
    <>
      <ConfirmAction
        label={label}
        confirmation={t('import.cancel_confirm')}
        pending={cancel.isPending}
        onConfirm={() => {
          beforeCancel?.();
          cancel.mutate({ params: { path: { id } } });
        }}
      />
      {problem ? <ProblemMessage problem={problem} /> : null}
    </>
  );
}
