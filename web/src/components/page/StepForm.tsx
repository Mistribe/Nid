import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Check } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { FormActions } from './FormPage';
import '@/locales/forms';

/** A form in steps: « Continuer » validates the step's fields and stays, focusing the first invalid one, when they are not valid. */
export function StepForm({ steps, trigger, onSubmit, submitLabel, pending, reveal, onAdvance }: {
  steps: { id: string; title: string; fields: string[]; content: ReactNode }[];
  trigger: (fields: string[]) => Promise<boolean>; onSubmit: () => void; submitLabel: string; pending?: boolean;
  /** A refusal that lands after the last step (the server's) on an earlier one: goes back to that step and focuses the first invalid field. */
  reveal?: { step: string } | null;
  /** Called with the step's id once it is valid, just before the next one shows (a wizard that derives values from a step). */
  onAdvance?: (id: string) => void;
}) {
  const { t } = useTranslation();
  const [index, setIndex] = useState(0);
  const [refused, setRefused] = useState(0);
  const body = useRef<HTMLDivElement>(null);
  const step = steps[index]!;
  const last = index === steps.length - 1;

  // After the commit that shows the errors.
  useEffect(() => { if (refused > 0) body.current?.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus(); }, [refused]);

  useEffect(() => {
    const at = reveal ? steps.findIndex((s) => s.id === reveal.step) : -1;
    if (at < 0) return;
    setIndex(at);
    setRefused((n) => n + 1);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- a new `reveal` object is the event; `steps` changes every render.
  }, [reveal]);

  const submit = async () => {
    if (last) return onSubmit();
    if (await trigger(step.fields)) { onAdvance?.(step.id); setIndex(index + 1); }
    else setRefused((n) => n + 1);
  };

  return (
    <form noValidate className="space-y-6" onSubmit={(event) => { event.preventDefault(); event.stopPropagation(); void submit(); }}>
      {/* A stepper: each step a numbered dot (a tick once passed), joined by a line that fills as the person goes. */}
      <nav aria-label={t('wizard.steps')}>
        <ol className="flex flex-wrap items-center gap-x-2 gap-y-2 text-sm">
          {steps.map((s, i) => (
            <li key={s.id} aria-current={i === index ? 'step' : undefined}
              className={cn('flex items-center gap-2', i === index ? 'font-semibold' : 'text-muted-foreground')}>
              {i > 0 ? <span aria-hidden="true" className={cn('h-px w-6 transition-colors', i <= index ? 'bg-primary' : 'bg-border')} /> : null}
              <span aria-hidden="true" className={cn('flex size-6 shrink-0 items-center justify-center rounded-full text-xs font-medium transition-colors',
                i < index ? 'bg-primary text-primary-foreground' : i === index ? 'border-2 border-primary text-primary' : 'border text-muted-foreground')}>
                {i < index ? <Check className="size-3.5" /> : i + 1}
              </span>
              {s.title}
            </li>
          ))}
        </ol>
      </nav>
      <p className="sr-only" aria-live="polite">{t('wizard.step_progress', { n: index + 1, total: steps.length })}</p>
      <div ref={body} className="space-y-4">{step.content}</div>
      <FormActions className="justify-between">
        {index > 0 ? <Button type="button" variant="outline" onClick={() => setIndex(index - 1)}>{t('wizard.back')}</Button> : <span />}
        <Button key={step.id} type="submit" disabled={pending}>{last ? submitLabel : t('wizard.next')}</Button>
      </FormActions>
    </form>
  );
}
