type Message = { id?: string; ok?: boolean; value?: unknown; error?: { code: string; message: string }; type?: string; url?: string; message?: string };
type WebView = { postMessage(value: unknown): void; addEventListener(type: "message", callback: (event: { data: Message }) => void): void };
const host = (window as unknown as { chrome?: { webview?: WebView } }).chrome?.webview;
const pending = new Map<string, { resolve: (value: unknown) => void; reject: (error: Error) => void }>();
const listeners = new Set<(value: Message) => void>();
export const isNative = !!host;
host?.addEventListener("message", event => {
  const message = event.data;
  if (message.id && pending.has(message.id)) {
    const request = pending.get(message.id)!; pending.delete(message.id);
    if (message.ok) request.resolve(message.value);
    else request.reject(Object.assign(new Error(message.error?.message ?? "操作を完了できませんでした。"), { code: message.error?.code }));
  } else listeners.forEach(listener => listener(message));
});
export function native<T = unknown>(op: string, body: unknown = {}): Promise<T> {
  if (!host) return Promise.reject(new Error("mcmere Playのデスクトップアプリで開いてください。"));
  const id = crypto.randomUUID();
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: value => resolve(value as T), reject });
    host.postMessage({ id, op, body });
  });
}
export function subscribe(listener: (value: Message) => void) { listeners.add(listener); return () => { listeners.delete(listener); }; }
