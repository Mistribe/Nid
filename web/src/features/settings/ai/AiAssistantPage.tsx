import { Link } from '@tanstack/react-router';
import { useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { aiProblem, aiReason } from '@/api/ai';
import { itemRoute } from '@/components/ItemLink';
import { Field } from '@/components/form/Field';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import { SettingsFrame } from '../SettingsFrame';
import { assistantTurn, type AssistantLink } from './assistantStream';
import '@/locales/ai';

type Turn = {
  question: string;
  answer: string;
  tools: string[];
  heldBack?: { count: number; destination: string; placement: string };
  done?: { provider: string; placement: string; model: string; reason: string; links: AssistantLink[] };
  code?: string;
  problem?: unknown;
};

/**
 * Réglages › IA › Assistant (#501): a question, the answer streamed as plain text, the tools called, what the household's rules
 * kept out, and the provider and model. The links are Nid's, built from the ids its tools returned; a URL in the answer stays
 * text (decision P). The turns of the page are one conversation, kept for its author (#531); a reload starts a new one, and
 * the panel (#534) will list them.
 */
export function AiAssistantPage() {
  const { t } = useTranslation();
  const api = useApi();
  const [question, setQuestion] = useState('');
  const [turns, setTurns] = useState<Turn[]>([]);
  const [conversation, setConversation] = useState<string | null>(null);
  const [running, setRunning] = useState(false);
  const field = useRef<HTMLTextAreaElement>(null);

  const update = (change: (turn: Turn) => Turn) => setTurns((all) => [...all.slice(0, -1), change(all[all.length - 1]!)]);

  const ask = async (event: FormEvent) => {
    event.preventDefault();
    const asked = question.trim();
    if (!asked || running) return;
    setTurns((all) => [...all, { question: asked, answer: '', tools: [] }]);
    setQuestion('');
    setRunning(true);
    try {
      for await (const e of assistantTurn(api, asked, conversation)) {
        if (e.type === 'text') update((turn) => ({ ...turn, answer: turn.answer + e.text }));
        else if (e.type === 'tool') update((turn) => ({ ...turn, tools: [...turn.tools, e.tool] }));
        else if (e.type === 'held_back') update((turn) => ({ ...turn, heldBack: e }));
        else if (e.type === 'done') {
          update((turn) => ({ ...turn, done: e }));
          if (e.conversationId) setConversation(e.conversationId);
        }
        else update((turn) => ({ ...turn, code: e.code }));
      }
    } catch (problem) {
      update((turn) => ({ ...turn, problem }));
    } finally {
      setRunning(false);
      field.current?.focus();
    }
  };

  const destination = (placement: string, name: string) => placement === 'cloud'
    ? t(`ai.cloud.provider.${name}` as 'ai.cloud.provider.anthropic', { defaultValue: name })
    : name;

  return (
    <SettingsFrame title={t('ai.assistant.title')}>
      <p className="max-w-prose text-sm text-muted-foreground">{t('ai.assistant.intro')}</p>
      <ol className="space-y-6" aria-live="polite">
        {turns.map((turn, i) => (
          <li key={i} className="space-y-2">
            <p className="font-medium whitespace-pre-wrap">{turn.question}</p>
            {turn.tools.length > 0 ? (
              <p className="text-sm text-muted-foreground">{t('ai.assistant.tools', { tools: turn.tools.join(', ') })}</p>
            ) : null}
            {turn.answer ? <p className="whitespace-pre-wrap">{turn.answer}</p> : null}
            {turn.done && turn.done.links.length > 0 ? (
              <ul className="flex flex-wrap gap-x-4 gap-y-1 text-sm" aria-label={t('ai.assistant.links')}>
                {turn.done.links.map((link) => (
                  <li key={link.id}><Link {...linkTo(link)} className="underline underline-offset-4">{link.name}</Link></li>
                ))}
              </ul>
            ) : null}
            {turn.heldBack ? (
              <p className="text-sm">
                {t('ai.assistant.held_back', { count: turn.heldBack.count, destination: destination(turn.heldBack.placement, turn.heldBack.destination) })}{' '}
                <Link to="/settings/ai" className="underline underline-offset-4">{t('ai.assistant.rules')}</Link>
              </p>
            ) : null}
            {turn.done && turn.done.reason !== 'end' ? (
              <p className="text-sm text-status-warning-text">{t(`ai.assistant.stop_${turn.done.reason}` as 'ai.assistant.stop_max_steps', { defaultValue: turn.done.reason })}</p>
            ) : null}
            {turn.done && turn.done.model ? (
              <p className="text-xs text-muted-foreground">{t('ai.assistant.answered_by', { provider: destination(turn.done.placement, turn.done.provider), model: turn.done.model })}</p>
            ) : null}
            {turn.code ? <p role="alert" className="text-sm text-destructive">{aiReason(t, turn.code)}</p> : null}
            {turn.problem ? <ProblemMessage problem={aiProblem(t, turn.problem)} /> : null}
          </li>
        ))}
      </ol>
      <form onSubmit={(e) => void ask(e)} className="space-y-3">
        <Field label={t('ai.assistant.question')} hint={t('ai.assistant.question_hint')}>
          {(c) => <Textarea {...c} ref={field} value={question} maxLength={4000} onChange={(e) => setQuestion(e.target.value)} />}
        </Field>
        <Button type="submit" disabled={running || !question.trim()}>{running ? t('ai.assistant.asking') : t('ai.assistant.ask')}</Button>
      </form>
    </SettingsFrame>
  );
}

/** A nanny has her own page; every other item goes by its module (`itemRoute`). */
function linkTo(link: AssistantLink) {
  return link.module === 'nannies'
    ? { to: '/childcare/nannies/$id', params: { id: link.id } } as const
    : itemRoute(link.module, link.id);
}
