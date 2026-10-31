<script lang="ts">
  // Every timeline of the show, by folder: run one (a new copy each time, as a show button does), stop it and its
  // copies, or open it in the editor (it becomes the one the transport works).
  import Icon from './Icon.svelte';
  import { live } from './lib/live.svelte.ts';

  const byFolder = $derived.by(() => {
    const groups = new Map<string, { id: string; name: string }[]>();
    for (const t of live.show?.timelines ?? []) {
      const list = groups.get(t.folder) ?? [];
      list.push({ id: t.id, name: t.name });
      groups.set(t.folder, list);
    }
    const compare = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: 'base', numeric: true });
    return [...groups.entries()]
      .sort(([a], [b]) => (a === '' ? -1 : b === '' ? 1 : compare(a, b)))
      .map(([folder, list]) => ({ folder, list: list.sort((a, b) => compare(a.name, b.name)) }));
  });

  const running = $derived(new Map(live.running.map((r) => [r.timeline.toLowerCase(), r.state])));
  const focusedId = $derived(live.focused?.timeline.toLowerCase());
</script>

<section class="card">
  <h2>Timelines</h2>
  {#each byFolder as group (group.folder)}
    {#if group.folder}<h3>{group.folder}</h3>{/if}
    <ul>
      {#each group.list as t (t.id)}
        {@const state = running.get(t.id.toLowerCase())}
        <li class:focused={focusedId === t.id.toLowerCase()}>
          <span class="dot" class:on={state === 'playing'} class:paused={state === 'paused'} aria-hidden="true"></span>
          <span class="name">{t.name}</span>
          <button class="square" aria-label="Open {t.name} in the editor" title="Open in the editor" onclick={() => live.send({ action: 'open', timeline: t.id })}>
            <Icon name="open" size={18} />
          </button>
          <button class="square" aria-label="Stop {t.name}" title="Stop it (and its copies)" disabled={state === undefined} onclick={() => live.send({ action: 'stopTimeline', timeline: t.id })}>
            <Icon name="stop" size={18} />
          </button>
          <button class="square go" aria-label="Run {t.name}" title="Run it from the start" onclick={() => live.send({ action: 'run', timeline: t.id })}>
            <Icon name="play" size={18} />
          </button>
        </li>
      {/each}
    </ul>
  {/each}
</section>

<style>
  h3 {
    margin: 14px 0 6px;
    font-size: 13px;
    color: var(--muted);
    font-weight: 600;
  }
  ul {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 4px;
  }
  li {
    display: grid;
    grid-template-columns: auto 1fr auto auto auto;
    align-items: center;
    gap: 8px;
    min-height: 44px;
    padding: 0 4px;
    border-radius: 8px;
  }
  li.focused {
    background: var(--raised);
  }
  .dot {
    width: 9px;
    height: 9px;
    border-radius: 50%;
    background: var(--line);
  }
  .dot.on {
    background: var(--ok);
  }
  .dot.paused {
    background: var(--warn);
  }
  .go {
    color: var(--ok);
  }
</style>
