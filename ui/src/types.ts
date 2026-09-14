export type Source = { kind: "modrinth" | "direct" | "hosted" | "manual"; projectId?: string; pageUrl?: string };
export type PackFile = { id: string; path: string; name: string; version: string; length: number; sha512: string; source: Source;
  requirement: "required" | "recommended"; defaultEnabled: boolean; modIds: string[]; requires: string[] };
export type Manifest = { releaseId: string; sequence: number; serverName: string; displayVersion: string; changelog: string;
  minecraftVersion: string; loader: { version: string }; java: { major: number }; files: PackFile[] };
export type SavedServer = { id: string; name: string; target: { origin: string; publicId: string }; playerName: string | null;
  memoryMiB: number; optionalChoices: Record<string, boolean> | null; gameObserved: boolean };
export type ServerView = { id: string; stage: string; error: string | null; errorCode: string | null; manifest: Manifest | null;
  status: { gameState: string; compatibilityState: string } | null;
  plan: { changes: { scope: string; path: string; action: string }[]; unknownMods: string[]; requiredFreeBytes: number; selectedIds: string[] } | null;
  javaReady: boolean; prismReady: boolean; directory: string | null; received: number; total: number; currentFile: string | null };
export type View = { settings: { theme: string; selectedServer: string | null; servers: SavedServer[] }; servers: ServerView[];
  busy: boolean; canCancel: boolean; version: string; update?: { stage: string; version: string | null; notes: string | null; length: number; received: number; queued: boolean; error: string | null };
  dataRoot?: string; migration?: { stage: string; copiedBytes: number; totalBytes: number; completedFiles: number; totalFiles: number } | null;
  activity: { gameRunning: boolean; prismRunning: boolean; uncertain: boolean } };
export type MigrationPlan = { id: string; source: string; destination: string; bytes: number; requiredFreeBytes: number; files: number; prismLoginRequired: boolean };
export type Discovery = { target: { origin: string; publicId: string }; info: { name: string; signingKey: { keyId: string } } };
export function size(bytes: number) { return bytes >= 1073741824 ? (bytes / 1073741824).toFixed(1) + " GiB" : bytes >= 1048576 ? (bytes / 1048576).toFixed(1) + " MiB" : Math.ceil(bytes / 1024) + " KiB"; }
export function choiceKey(file: PackFile) { return file.source.projectId ? "project:" + file.source.projectId : file.modIds.length ? "mods:" + [...file.modIds].sort().join(",") : "path:" + file.path; }
export function selection(files: PackFile[], server: SavedServer, requiredOnly = false) {
  const ids = new Set<string>();
  const add = (file: PackFile) => { if (ids.has(file.id)) return; ids.add(file.id); file.requires.forEach(id => { const dependency = files.find(item => item.id === id); if (dependency) add(dependency); }); };
  files.forEach(file => { if (file.requirement === "required" || (!requiredOnly && (server.optionalChoices?.[choiceKey(file)] ?? file.defaultEnabled))) add(file); });
  return ids;
}
