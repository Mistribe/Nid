import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';

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
      <nav aria-label={t('wizard.steps')}>
        <ol className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
          {steps.map((s, i) => (
            <li key={s.id} aria-current={i === index ? 'step' : undefined} className={i === index ? 'font-semibold' : 'text-muted-foreground'}>
              {i + 1}. {s.title}
            </li>
          ))}
        </ol>
      </nav>
      <p className="sr-only" aria-live="polite">{t('wizard.step_progress', { n: index + 1, total: steps.length })}</p>
      <div ref={body} className="space-y-4">{step.content}</div>
      <div className="flex justify-between gap-2">
        {index > 0 ? <Button type="button" variant="outline" onClick={() => setIndex(index - 1)}>{t('wizard.back')}</Button> : <span />}
        <Button key={step.id} type="submit" disabled={pending}>{last ? submitLabel : t('wizard.next')}</Button>
      </div>
    </form>
  );
}
