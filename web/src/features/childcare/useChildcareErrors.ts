import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import type { FieldValues, Path, UseFormSetError } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { toProblem } from '@/api/errors';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { invalidateChildcare } from './api';
import '@/locales/childcare';

/**
 * A refused write where it belongs: a 422 on its field, a field the form does not hold as its own message (`unplaced`), a stale
 * version as « Cette garde a changé entre-temps » (and the arrangement refetched).
 */
export function useChildcareErrors<T extends FieldValues>(setError: UseFormSetError<T>, path: (path: string) => Path<T> | undefined) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const message = (f: { code: string; message: string }) => t(`childcare_form.error.${f.code.replaceAll('.', '_')}`, { defaultValue: f.message });
  const base = useSubmitErrors(setError, path, message);
  const [stale, setStale] = useState(false);
  const [unplaced, setUnplaced] = useState<string[]>([]);
  return {
    // Field errors the form has no field for are listed as such; the generic message would hide their reason.
    problem: stale || unplaced.length > 0 ? null : base.problem,
    stale,
    unplaced,
    clear: () => { base.clear(); setStale(false); setUnplaced([]); },
    onError(error: unknown) {
      const conflict = toProblem(error).code === 'childcare.conflict';
      setStale(conflict);
      if (conflict) { void invalidateChildcare(queryClient); return; }
      base.onError(error);
      setUnplaced(fieldErrorsOf(error).filter((f) => !path(f.path)).map(message));
    },
  };
}
