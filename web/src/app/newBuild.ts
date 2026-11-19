import i18n from './i18n';

type Reloadable = { reload: () => void };

/** The tab still runs the build it loaded before the server was updated: offer the reload, never force it (a form may be open). */
export async function announceNewVersion(location: Reloadable): Promise<void> {
  const { toast } = await import('sonner');
  toast.info(i18n.t('app.new_version'), {
    id: 'new-version',
    duration: Infinity,
    action: { label: i18n.t('app.reload'), onClick: () => location.reload() },
  });
}

const RELOADED = 'nid-reloaded-for';

/**
 * `vite:preloadError`: a page's chunk is gone once the server is updated (hashed names), so load the new build. Once per
 * build, so a broken deploy cannot loop; without storage there is no such guard, and the error shows instead.
 */
export function reloadOnMissingChunk(event: Event, build: string, storage: () => Storage, location: Reloadable): void {
  try {
    const store = storage();
    if (store.getItem(RELOADED) === build) return;
    store.setItem(RELOADED, build);
  } catch {
    return;
  }
  event.preventDefault();
  location.reload();
}
