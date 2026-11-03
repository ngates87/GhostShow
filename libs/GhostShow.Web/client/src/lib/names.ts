import type { Show, Timeline } from './show.ts';

/** A timeline's place in the tree ("Rooms/Graveyard scare"), or a stand-in if it's not in the show. */
export function pathOf(show: Show | null, id: string): string {
  const timeline = find(show, id);
  if (!timeline) return 'A timeline';
  return timeline.folder ? `${timeline.folder}/${timeline.name}` : timeline.name;
}

export function find(show: Show | null, id: string): Timeline | undefined {
  const key = id.toLowerCase();
  return show?.timelines.find((t) => t.id.toLowerCase() === key);
}
