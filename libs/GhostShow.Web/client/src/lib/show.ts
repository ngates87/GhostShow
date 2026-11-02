// The device's copy of the show, kept the same as the show computer's by applying each change's operations, exactly
// as GhostShow.Core's ShowDocument applies them (see Editing/EditOps.cs and ModelPaths.cs):
// - a path addresses an object from the show down, '/'-separated: a property by its camelCase name, a list item by its
//   ID if items have one ("id"), otherwise by its index;
// - set: a property; insert: into a list at an index (-1 or past the end appends); remove: a list item; move: a list
//   item to an index in its list.

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };
export type JsonObject = { [key: string]: Json };

export type EditOp =
  | { op: 'set'; path: string; value: Json }
  | { op: 'insert'; path: string; index: number; item: Json }
  | { op: 'remove'; path: string }
  | { op: 'move'; path: string; index: number };

export interface Timeline extends JsonObject {
  id: string;
  name: string;
  folder: string;
}

/** The show as the computer sends it: the project's fields, its timelines (each with its folder) and the tree's folders. */
export interface Show extends JsonObject {
  name: string;
  timelines: Timeline[];
  timelineFolders: string[];
}

export class EditError extends Error {}

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function indexOf(list: Json[], key: string): number {
  if (guid.test(key)) {
    const id = key.toLowerCase();
    return list.findIndex((item) => typeof item === 'object' && item !== null && !Array.isArray(item) && typeof item.id === 'string' && item.id.toLowerCase() === id);
  }
  const index = Number(key);
  return Number.isInteger(index) && index >= 0 && index < list.length ? index : -1;
}

function walk(root: Json, parts: string[], whole: string): Json {
  let current = root;
  for (const part of parts) {
    if (Array.isArray(current)) {
      const index = indexOf(current, part);
      if (index < 0) throw new EditError(`Can't edit ${whole}: there's no "${part}".`);
      current = current[index]!;
    } else if (typeof current === 'object' && current !== null && part in current) {
      current = current[part]!;
    } else {
      throw new EditError(`Can't edit ${whole}: there's no "${part}".`);
    }
  }
  return current;
}

function split(path: string): [string[], string] {
  const parts = path.length === 0 ? [] : path.split('/');
  const last = parts.pop();
  if (last === undefined) throw new EditError("Can't edit the show itself.");
  return [parts, last];
}

function listAt(root: Json, path: string): Json[] {
  const list = walk(root, path.length === 0 ? [] : path.split('/'), path);
  if (!Array.isArray(list)) throw new EditError(`Can't edit ${path}: it isn't a list.`);
  return list;
}

/** Applies one operation to the show, in place. */
export function apply(show: Json, op: EditOp): void {
  switch (op.op) {
    case 'set': {
      const [parent, name] = split(op.path);
      const owner = walk(show, parent, op.path);
      if (Array.isArray(owner)) {
        const index = indexOf(owner, name);
        if (index < 0) throw new EditError(`Can't edit ${op.path}: there's no "${name}".`);
        owner[index] = op.value;
      } else if (typeof owner === 'object' && owner !== null) {
        owner[name] = op.value;
      } else {
        throw new EditError(`Can't edit ${op.path}.`);
      }
      return;
    }
    case 'insert': {
      const list = listAt(show, op.path);
      const index = op.index < 0 || op.index > list.length ? list.length : op.index;
      list.splice(index, 0, op.item);
      return;
    }
    case 'remove':
    case 'move': {
      const [parent, key] = split(op.path);
      const list = listAt(show, parent.join('/'));
      const from = indexOf(list, key);
      if (from < 0) throw new EditError(`Can't edit ${op.path}: there's no such item.`);
      const [item] = list.splice(from, 1);
      if (op.op === 'move') list.splice(Math.min(Math.max(op.index, 0), list.length), 0, item!);
      return;
    }
  }
}

/** Applies a change's operations, in order. */
export function applyAll(show: Json, ops: EditOp[]): void {
  for (const op of ops) apply(show, op);
}

/** "01:23.4" (or "1:01:23.4" past an hour), as the desktop's transport shows times. */
export function formatTime(seconds: number): string {
  const tenths = Math.max(0, Math.floor(seconds * 10 + 1e-6));
  const s = Math.floor(tenths / 10), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
  const two = (n: number) => String(n).padStart(2, '0');
  const time = `${two(m)}:${two(s % 60)}.${tenths % 10}`;
  return h > 0 ? `${h}:${time}` : time;
}
