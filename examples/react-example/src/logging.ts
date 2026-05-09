const LOG_KEY = "react-example-logs";
const MAX_LOGS = 50;

type LogEntry = {
  id: string;
  message: string;
  context?: Record<string, unknown>;
  ts: number;
};

const hasCrypto = typeof crypto !== "undefined" && !!crypto.randomUUID;

export function initLogging() {
  // noop for now; local log buffer only
}

export function captureError(err: unknown, context?: Record<string, unknown>) {
  const id = hasCrypto ? crypto.randomUUID() : `evt-${Date.now()}`;
  const entry: LogEntry = {
    id,
    message: err instanceof Error ? err.message : String(err),
    context,
    ts: Date.now(),
  };

  try {
    const existing = readLogs();
    existing.unshift(entry);
    const trimmed = existing.slice(0, MAX_LOGS);
    localStorage.setItem(LOG_KEY, JSON.stringify(trimmed));
  } catch {
    // ignore storage errors
  }

  console.error(`[event:${id}]`, err, context);
  return id;
}

function readLogs(): LogEntry[] {
  try {
    const raw = localStorage.getItem(LOG_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    if (Array.isArray(parsed)) return parsed as LogEntry[];
    return [];
  } catch {
    return [];
  }
}
