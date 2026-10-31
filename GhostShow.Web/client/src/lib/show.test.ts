// Run with `npm test` (Node's own test runner; Node strips the types).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { apply, applyAll, EditError, formatTime, type Json } from './show.ts';

const a = '6f1c2a3b-0000-4000-8000-000000000001';
const b = '6f1c2a3b-0000-4000-8000-000000000002';

function show(): Json {
  return {
    name: 'Haunt',
    timelines: [
      {
        id: a,
        name: 'Main',
        folder: '',
        tracks: [{ id: b, name: 'Lights', events: [], channels: [{ id: 'c', points: [{ time: '00:00:00', value: 0 }, { time: '00:00:04', value: 255 }] }] }],
      },
    ],
    timelineFolders: [],
  };
}

test('set addresses properties by name and list items by ID or index', () => {
  const s = show();
  apply(s, { op: 'set', path: `timelines/${a}/tracks/${b}/name`, value: 'Spots' });
  apply(s, { op: 'set', path: `timelines/${a}/tracks/${b}/channels/0/points/1/value`, value: 128 });
  apply(s, { op: 'set', path: `timelines/${a.toUpperCase()}/folder`, value: 'Rooms' });
  const timeline = (s as any).timelines[0];
  assert.equal(timeline.tracks[0].name, 'Spots');
  assert.equal(timeline.tracks[0].channels[0].points[1].value, 128);
  assert.equal(timeline.folder, 'Rooms');
});

test('insert appends past the end, remove and move work by ID as the computer does', () => {
  const s = show();
  const events = `timelines/${a}/tracks/${b}/events`;
  applyAll(s, [
    { op: 'insert', path: events, index: -1, item: { id: 'e1', start: '00:00:01' } },
    { op: 'insert', path: events, index: 99, item: { id: '6f1c2a3b-0000-4000-8000-0000000000e2', start: '00:00:02' } },
    { op: 'insert', path: events, index: 0, item: { id: 'e0' } },
    { op: 'move', path: `${events}/6f1c2a3b-0000-4000-8000-0000000000e2`, index: 0 },
    { op: 'remove', path: `${events}/1` },
  ]);
  assert.deepEqual((s as any).timelines[0].tracks[0].events.map((e: any) => e.id), ['6f1c2a3b-0000-4000-8000-0000000000e2', 'e1']);
});

test("an operation that doesn't fit is an error", () => {
  assert.throws(() => apply(show(), { op: 'set', path: `timelines/${b}/name`, value: 'x' }), EditError);
  assert.throws(() => apply(show(), { op: 'remove', path: 'timelines/5' }), EditError);
});

test('times read as the transport shows them', () => {
  assert.equal(formatTime(0), '00:00.0');
  assert.equal(formatTime(83.46), '01:23.4');
  assert.equal(formatTime(3723.05), '1:02:03.0');
});
