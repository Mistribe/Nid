import { prepareFile, type Prepared } from './prepare';

/**
 * The preparation in a module Worker, so a large file does not freeze the page. jsdom has no Worker: the
 * component tests run it inline, which is the only place the fallback is taken.
 */
export function prepareInBackground(bytes: ArrayBuffer): Promise<Prepared> {
  if (typeof Worker === 'undefined') return prepareFile(bytes);
  return new Promise((resolve, reject) => {
    const worker = new Worker(new URL('./prepare.worker.ts', import.meta.url), { type: 'module' });
    worker.onmessage = (event: MessageEvent<Prepared>) => {
      worker.terminate();
      resolve(event.data);
    };
    worker.onerror = (event) => {
      worker.terminate();
      reject(new Error(event.message));
    };
    worker.postMessage(bytes, [bytes]);
  });
}
