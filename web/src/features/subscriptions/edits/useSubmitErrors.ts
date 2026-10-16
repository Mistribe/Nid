import { useState } from 'react';
import type { FieldValues, Path, UseFormSetError } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { present, toProblem, type FieldError, type Presentation } from '@/api/errors';
import { fieldErrorsOf, messageFor } from '../fieldErrors';

/**
 * A failed write, shown where it belongs (1E2a design §2.1): each field the server names on its field, anything else
 * (a conflict, a refusal no field owns) as one message above the form. `messageOf` may word a field's error for its screen.
 */
export function useSubmitErrors<T extends FieldValues>(
  setError: UseFormSetError<T>, paths: Record<string, Path<T>> | ((path: string) => Path<T> | undefined),
  messageOf?: (field: FieldError) => string | undefined,
) {
  const nameOf = (path: string) => (typeof paths === 'function' ? paths(path) : paths[path]);
  const { t } = useTranslation();
  const [problem, setProblem] = useState<Presentation | null>(null);

  const onError = (error: unknown) => {
    const fields = fieldErrorsOf(error);
    for (const field of fields) {
      const name = nameOf(field.path);
      if (name) setError(name, { type: 'server', message: messageOf?.(field) ?? messageFor(t, field) });
    }
    const placed = fields.length > 0 && fields.every((f) => nameOf(f.path));
    setProblem(placed ? null : present(toProblem(error)));
  };

  return { problem, onError, clear: () => setProblem(null), nameOf };
}
