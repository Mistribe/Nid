import { afterEach, describe, expect, it, vi } from 'vitest';
import { copyText } from './clipboard';

const clipboard = (value: unknown) => Object.defineProperty(navigator, 'clipboard', { value, configurable: true });
const copyCommand = (result: boolean | (() => boolean)) =>
  Object.defineProperty(document, 'execCommand', { value: vi.fn(typeof result === 'function' ? result : () => result), configurable: true });

describe('copyText', () => {
  afterEach(() => { clipboard(undefined); copyCommand(false); });

  it('writes through the clipboard when the page is secure', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    clipboard({ writeText });
    await copyText('https://nid.lan/feed.ics');
    expect(writeText).toHaveBeenCalledWith('https://nid.lan/feed.ics');
  });

  it('over plain HTTP, copies the selected text with the copy command and leaves nothing behind (#641)', async () => {
    clipboard(undefined);
    let selected = '';
    copyCommand(() => { selected = (document.activeElement as HTMLTextAreaElement).value; return true; });
    await copyText('nid_secret');
    expect(selected).toBe('nid_secret');
    expect(document.querySelector('textarea')).toBeNull();
  });

  it('falls back on the copy command when the clipboard refuses', async () => {
    clipboard({ writeText: vi.fn().mockRejectedValue(new Error('denied')) });
    copyCommand(true);
    await expect(copyText('x')).resolves.toBeUndefined();
  });

  it('rejects when the browser refuses both', async () => {
    clipboard(undefined);
    copyCommand(false);
    await expect(copyText('x')).rejects.toThrow();
  });
});
