// The connection to the show computer: pairing (once, with the PIN it shows), then a WebSocket that keeps this
// device's copy of the show live (see GhostShow.Web/RemoteServer.cs for the messages).

import { applyAll, EditError, type EditOp, type Show } from './show.ts';

export type Role = 'editor' | 'operator';

export interface Device {
  id: string;
  name: string;
  role: Role;
}

export interface PlaybackState {
  state: 'stopped' | 'playing' | 'paused';
  position: number;
  length: number;
  loop: boolean;
}

export interface Focused extends PlaybackState {
  timeline: string;
}

export interface Running extends PlaybackState {
  id: string;
  timeline: string;
  name: string;
  copy: boolean;
}

type Message =
  | { type: 'hello'; device: Device; version: number; show: Show; fullApp?: boolean }
  | { type: 'change'; version: number; kind: 'do' | 'undo' | 'redo'; description: string; origin: string | null; ops: EditOp[] }
  | { type: 'status'; focused: Focused | null; running: Running[]; warnings: string[] }
  | { type: 'device'; device: Device }
  | { type: 'result'; id: number; ok: boolean; error: string | null };

/** What a device can ask the show computer to do (see RemoteServer.Execute), with what each needs. */
export type Command =
  | { action: 'play' | 'pause' | 'stop' | 'restart' | 'stopAll' | 'blackout' }
  | { action: 'seek'; position: number }
  | { action: 'run' | 'stopTimeline' | 'open'; timeline: string }
  | { action: 'stopRunning'; running: string }
  | { action: 'press'; button: string };

/** The close code the server uses for "not paired" (RemoteServer.NotPaired). */
const NotPaired = 4401;
const TokenKey = 'ghostshow.token';

function storedToken(): string | null {
  try {
    return localStorage.getItem(TokenKey);
  } catch {
    return null;
  }
}

function storeToken(token: string | null): void {
  try {
    if (token === null) localStorage.removeItem(TokenKey);
    else localStorage.setItem(TokenKey, token);
  } catch {
    // Private browsing: it pairs again next time.
  }
}

/** A name for this device to start with ("iPad", "Mac", ...): it can be changed before pairing. */
export function guessDeviceName(): string {
  const agent = navigator.userAgent;
  if (/iPad/.test(agent) || (/Macintosh/.test(agent) && navigator.maxTouchPoints > 1)) return 'iPad';
  if (/iPhone/.test(agent)) return 'iPhone';
  if (/Android/.test(agent)) return /Mobile/.test(agent) ? 'Android phone' : 'Android tablet';
  if (/Macintosh/.test(agent)) return 'Mac';
  if (/Windows/.test(agent)) return 'Windows PC';
  if (/Linux/.test(agent)) return 'Linux PC';
  return 'Device';
}

class Live {
  /** Paired: has a token (it may not be connected right now). */
  paired = $state(storedToken() !== null);
  /** Connected and up to date with the show. */
  connected = $state(false);
  /** Why it isn't connected, when it has tried. */
  problem = $state<string | null>(null);
  device = $state<Device | null>(null);
  show = $state<Show | null>(null);
  version = 0;
  focused = $state<Focused | null>(null);
  running = $state<Running[]>([]);
  /** The last change made on the show (e.g. "Move clip", from the desktop). */
  lastChange = $state<string | null>(null);
  /** The full app (timelines, editing) is served at /app/ too. */
  fullApp = $state(false);
  /** The desktop's warnings (patch errors, failing outputs). */
  warnings = $state<string[]>([]);
  /** A short message to show for a moment: why a command couldn't be done. */
  toast = $state<string | null>(null);

  #nextId = 1;
  #pending = new Map<number, (error: string | null) => void>();
  #toastTimer: ReturnType<typeof setTimeout> | null = null;

  get isEditor(): boolean {
    return this.device?.role === 'editor';
  }

  /**
   * Asks the show computer to do something. Resolves with null when it's done, or why not (also shown as a toast).
   */
  send(command: Command): Promise<string | null> {
    const socket = this.#socket;
    if (socket === null || socket.readyState !== WebSocket.OPEN || !this.connected) {
      const why = 'Not connected to the show computer.';
      this.say(why);
      return Promise.resolve(why);
    }
    const id = this.#nextId++;
    socket.send(JSON.stringify({ type: 'command', id, ...command }));
    return new Promise((resolve) => {
      this.#pending.set(id, (error) => {
        if (error !== null) this.say(error);
        resolve(error);
      });
    });
  }

  say(message: string): void {
    this.toast = message;
    if (this.#toastTimer !== null) clearTimeout(this.#toastTimer);
    this.#toastTimer = setTimeout(() => (this.toast = null), 4000);
  }

  #socket: WebSocket | null = null;
  #retry = 0;
  #timer: ReturnType<typeof setTimeout> | null = null;

  /** Pairs with the PIN shown on the show computer, then connects. Returns why not, if it didn't. */
  async pair(pin: string, name: string, role: Role): Promise<string | null> {
    let response: Response;
    try {
      response = await fetch('/api/pair', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ pin, name, role }),
      });
    } catch {
      return "Can't reach the show computer. Is this device on the show's network?";
    }
    const body = await response.json().catch(() => ({}));
    if (!response.ok) return body.error ?? `The show computer said no (${response.status}).`;
    storeToken(body.token);
    this.paired = true;
    this.problem = null;
    this.connect();
    return null;
  }

  /** Forgets the pairing on this device (the show computer can remove it too). */
  unpair(): void {
    storeToken(null);
    this.paired = false;
    this.#close();
    this.show = null;
    this.device = null;
  }

  connect(): void {
    const token = storedToken();
    if (token === null || this.#socket !== null) return;
    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const socket = new WebSocket(`${protocol}//${location.host}/api/live?token=${encodeURIComponent(token)}`);
    this.#socket = socket;
    socket.onmessage = (event) => this.#receive(JSON.parse(event.data) as Message);
    socket.onclose = (event) => {
      if (this.#socket !== socket) return;
      this.#socket = null;
      this.connected = false;
      for (const done of this.#pending.values()) done('The connection dropped before the show computer answered.');
      this.#pending.clear();
      if (event.code === NotPaired) {
        this.problem = event.reason || 'This device is no longer paired with the show computer.';
        storeToken(null);
        this.paired = false;
        return;
      }
      this.problem = event.reason || 'Lost the connection to the show computer. Trying again…';
      this.#scheduleReconnect();
    };
  }

  #receive(message: Message): void {
    switch (message.type) {
      case 'hello':
        this.device = message.device;
        this.show = message.show;
        this.fullApp = message.fullApp === true;
        this.version = message.version;
        this.connected = true;
        this.problem = null;
        this.#retry = 0;
        return;
      case 'change':
        if (this.show === null) return;
        if (message.version !== this.version + 1) {
          this.#resync('Missed a change: getting the show again.');
          return;
        }
        try {
          applyAll(this.show, message.ops);
        } catch (error) {
          if (error instanceof EditError) {
            this.#resync('A change didn\'t fit: getting the show again.');
            return;
          }
          throw error;
        }
        this.version = message.version;
        this.lastChange = message.kind === 'do' ? message.description : `${message.kind === 'undo' ? 'Undo' : 'Redo'} ${message.description}`;
        return;
      case 'status':
        this.focused = message.focused;
        this.running = message.running;
        this.warnings = message.warnings ?? [];
        return;
      case 'device':
        this.device = message.device;
        return;
      case 'result': {
        const done = this.#pending.get(message.id);
        this.#pending.delete(message.id);
        done?.(message.ok ? null : (message.error ?? 'The show computer said no.'));
        return;
      }
    }
  }

  /** Out of step with the show: connect again, which sends the whole show. */
  #resync(why: string): void {
    this.problem = why;
    this.#close();
    this.connect();
  }

  #close(): void {
    const socket = this.#socket;
    this.#socket = null;
    this.connected = false;
    if (this.#timer !== null) clearTimeout(this.#timer);
    this.#timer = null;
    socket?.close();
  }

  #scheduleReconnect(): void {
    if (this.#timer !== null) return;
    const delay = Math.min(5000, 500 * 2 ** this.#retry++);
    this.#timer = setTimeout(() => {
      this.#timer = null;
      this.connect();
    }, delay);
  }
}

export const live = new Live();

// Back on the page (an iPad waking up, a tab coming to the front): reconnect straight away rather than waiting.
if (typeof document !== 'undefined') {
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && live.paired && !live.connected) live.connect();
  });
}
