import { useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { AI_SETTINGS, aiProblem, aiReason, ASSISTANT_ME } from '@/api/ai';
import { useSession } from '@/app/Providers';
import { ConfirmAction } from '@/components/ConfirmAction';
import { Field } from '@/components/form/Field';
import { itemRoute } from '@/components/ItemLink';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet';
import { Textarea } from '@/components/ui/textarea';
import { formatInstant } from '@/lib/format';
import { assistantTurn, type AssistantLink } from './assistantStream';
import '@/locales/ai';

const CONVERSATIONS = '/api/v1/ai/assistant/conversations' as const;
const CONVERSATION = '/api/v1/ai/assistant/conversations/{id}' as const;

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
 * The assistant, from every page (#534, docs/ai/README.md §8.3, §11): the current conversation streamed as plain text, with
 * the tools it called, Nid's links (a URL the model writes stays text, decision P), what the household's rules kept out, and
 * who answered; the earlier conversations, reopened or deleted. When it cannot answer, it says why and who can fix it.
 */
export function AssistantPanel({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useTranslation();
  const [listing, setListing] = useState(false);
  const [conversation, setConversation] = useState<string | null>(null);
  const [turns, setTurns] = useState<Turn[]>([]);

  const start = (id: string | null, kept: Turn[]) => { setConversation(id); setTurns(kept); setListing(false); };

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent side="right" className="flex w-full flex-col gap-0 sm:max-w-lg">
        <SheetHeader className="border-b">
          <SheetTitle>{t('ai.panel.title')}</SheetTitle>
          <SheetDescription>{t('ai.panel.description')}</SheetDescription>
          <div className="flex flex-wrap gap-2 pt-2">
            <Button type="button" variant="outline" size="sm" onClick={() => start(null, [])}>{t('ai.panel.new')}</Button>
            <Button type="button" variant="outline" size="sm" aria-pressed={listing} onClick={() => setListing(!listing)}>{t('ai.panel.history')}</Button>
          </div>
        </SheetHeader>
        <div className="flex-1 overflow-y-auto p-4">
          {listing ? <ConversationList current={conversation} onOpen={start} />
            : <Conversation key={conversation ?? 'new'} id={conversation} turns={turns} setTurns={setTurns} onKept={setConversation} />}
        </div>
      </SheetContent>
    </Sheet>
  );
}

/** Why the assistant cannot answer now, and who can fix it (§11); null when it can try. */
function useBlocked(): { text: string; link: boolean } | null {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { capabilities } = useSession();
  const me = $api.useQuery('get', ASSISTANT_ME);
  const settings = $api.useQuery('get', AI_SETTINGS);
  if (me.data && !me.data.enabled) return { text: t('ai.panel.off'), link: true };
  if (settings.data && !settings.data.uses.find((u) => u.use === 'chat')?.model)
    return capabilities.admin ? { text: t('ai.panel.no_model_admin'), link: true } : { text: t('ai.panel.no_model'), link: false };
  return null;
}

function Conversation({ id, turns, setTurns, onKept }: {
  id: string | null; turns: Turn[]; setTurns: (change: (all: Turn[]) => Turn[]) => void; onKept: (id: string) => void;
}) {
  const { t } = useTranslation();
  const api = useApi();
  const queryClient = useQueryClient();
  const blocked = useBlocked();
  const [question, setQuestion] = useState('');
  const [running, setRunning] = useState(false);
  const field = useRef<HTMLTextAreaElement>(null);
  const kept = useRef(id);

  const update = (change: (turn: Turn) => Turn) => setTurns((all) => [...all.slice(0, -1), change(all[all.length - 1]!)]);

  const ask = async (event: FormEvent) => {
    event.preventDefault();
    const asked = question.trim();
    if (!asked || running) return;
    setTurns((all) => [...all, { question: asked, answer: '', tools: [] }]);
    setQuestion('');
    setRunning(true);
    try {
      for await (const e of assistantTurn(api, asked, kept.current)) {
        if (e.type === 'text') update((turn) => ({ ...turn, answer: turn.answer + e.text }));
        else if (e.type === 'tool') update((turn) => ({ ...turn, tools: [...turn.tools, e.tool] }));
        else if (e.type === 'held_back') update((turn) => ({ ...turn, heldBack: e }));
        else if (e.type === 'done') {
          update((turn) => ({ ...turn, done: e }));
          if (e.conversationId) {
            kept.current = e.conversationId;
            onKept(e.conversationId);
            void queryClient.invalidateQueries({ queryKey: ['get', CONVERSATIONS] });
          }
        } else update((turn) => ({ ...turn, code: e.code }));
      }
    } catch (problem) {
      update((turn) => ({ ...turn, problem }));
    } finally {
      setRunning(false);
      field.current?.focus();
    }
  };

  return (
    <div className="space-y-6">
      {blocked ? (
        <p role="status" className="text-sm">
          {blocked.text}{' '}
          {blocked.link ? <Link to="/settings/ai" className="underline underline-offset-4">{t('ai.panel.settings')}</Link> : null}
        </p>
      ) : null}
      <ol className="space-y-6" aria-live="polite">
        {turns.map((turn, i) => <TurnView key={i} turn={turn} />)}
      </ol>
      <form onSubmit={(e) => void ask(e)} className="space-y-3">
        <Field label={t('ai.assistant.question')} hint={t('ai.assistant.question_hint')}>
          {(c) => <Textarea {...c} ref={field} value={question} maxLength={4000} onChange={(e) => setQuestion(e.target.value)} />}
        </Field>
        <Button type="submit" disabled={running || !question.trim() || blocked !== null}>{running ? t('ai.assistant.asking') : t('ai.assistant.ask')}</Button>
      </form>
    </div>
  );
}

function TurnView({ turn }: { turn: Turn }) {
  const { t } = useTranslation();
  const destination = (placement: string, name: string) => placement === 'cloud'
    ? t(`ai.cloud.provider.${name}` as 'ai.cloud.provider.anthropic', { defaultValue: name })
    : name;
  return (
    <li className="space-y-2">
      <p className="font-medium whitespace-pre-wrap">{turn.question}</p>
      {turn.tools.length > 0 ? <p className="text-sm text-muted-foreground">{t('ai.assistant.tools', { tools: turn.tools.join(', ') })}</p> : null}
      {turn.answer ? <p className="whitespace-pre-wrap">{turn.answer}</p> : null}
      {turn.done && turn.done.links.length > 0 ? (
        <ul className="flex flex-wrap gap-x-4 gap-y-1 text-sm" aria-label={t('ai.assistant.links')}>
          {turn.done.links.map((link) => <li key={link.id}><Link {...linkTo(link)} className="underline underline-offset-4">{link.name}</Link></li>)}
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
  );
}

/** The earlier conversations, the author's own, newest first, a page at a time (#531). */
function ConversationList({ current, onOpen }: { current: string | null; onOpen: (id: string | null, kept: Turn[]) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [page, setPage] = useState(1);
  const list = $api.useQuery('get', CONVERSATIONS, { params: { query: { page } } });
  const remove = $api.useMutation('delete', CONVERSATION, {
    onSuccess: (_, { params }) => {
      void queryClient.invalidateQueries({ queryKey: ['get', CONVERSATIONS] });
      if (params.path.id === current) onOpen(null, []);
    },
  });
  const read = async (id: string) => {
    const data = await queryClient.fetchQuery($api.queryOptions('get', CONVERSATION, { params: { path: { id } } }));
    const kept: Turn[] = [];
    for (const message of data.messages) {
      if (message.role === 'user') kept.push({ question: message.content, answer: '', tools: [] });
      else if (kept.length > 0) kept[kept.length - 1] = { ...kept[kept.length - 1]!, answer: message.content, tools: [...message.tools] };
    }
    onOpen(id, kept);
  };

  if (list.error) return <ProblemMessage problem={aiProblem(t, list.error)} />;
  if (!list.data) return <p className="text-sm text-muted-foreground">{t('ai.loading')}</p>;
  if (list.data.total === 0) return <p className="text-sm text-muted-foreground">{t('ai.panel.empty')}</p>;
  return (
    <div className="space-y-4">
      <ul className="divide-y" aria-label={t('ai.panel.history')}>
        {list.data.items.map((item) => (
          <li key={item.id} className="flex items-center gap-2 py-2">
            <button type="button" className="min-w-0 flex-1 text-left hover:underline" onClick={() => void read(item.id)}>
              <span className="block truncate">{item.title}</span>
              <span className="block text-xs text-muted-foreground">{formatInstant(item.updatedAt)}</span>
            </button>
            <ConfirmAction label={t('ai.panel.delete')} title={t('ai.panel.delete_title', { title: item.title })} confirmation={t('ai.panel.delete_confirm')} pending={remove.isPending}
              onConfirm={() => remove.mutate({ params: { path: { id: item.id } } })} />
          </li>
        ))}
      </ul>
      {list.data.total > page * 30 ? <Button type="button" variant="outline" size="sm" onClick={() => setPage(page + 1)}>{t('ai.panel.older')}</Button> : null}
    </div>
  );
}

/** A nanny has her own page; every other item goes by its module (`itemRoute`). */
function linkTo(link: AssistantLink) {
  return link.module === 'nannies'
    ? { to: '/childcare/nannies/$id', params: { id: link.id } } as const
    : itemRoute(link.module, link.id);
}
