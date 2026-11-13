<script lang="ts">
  // Everything playing on the show computer (copies too), each with Stop, and Stop all.
  import Icon from './Icon.svelte';
  import { live } from './lib/live.svelte.ts';
  import { pathOf } from './lib/names.ts';
  import { formatTime } from './lib/show.ts';

  const stateLabel = { playing: 'Playing', paused: 'Paused', stopped: 'Stopped' } as const;
</script>

<section class="card">
  <div class="head">
    <h2>Playing</h2>
    <button class="text" disabled={live.running.length === 0} onclick={() => live.send({ action: 'stopAll' })}>Stop all</button>
  </div>
  {#if live.running.length === 0}
    <p class="muted">Nothing is playing.</p>
  {:else}
    <ul>
      {#each live.running as r (r.id)}
        <li>
          <span class="state {r.state}">{stateLabel[r.state]}</span>
          <span class="name">{pathOf(live.show, r.timeline)}{r.copy ? ' (copy)' : ''}</span>
          <span class="flags">
            {#if r.loop}<span title="Looped: it plays until it's stopped" aria-label="looped"><Icon name="loop" size={16} /></span>{/if}
            {#if r.oneOnly}<span title="One only: running it again while it plays does nothing" aria-label="one only"><Icon name="oneOnly" size={16} /></span>{/if}
          </span>
          <span class="pos">{formatTime(r.position)}</span>
          <button class="square" aria-label="Stop {pathOf(live.show, r.timeline)}" onclick={() => live.send({ action: 'stopRunning', running: r.id })}>
            <Icon name="stop" size={18} />
          </button>
        </li>
      {/each}
    </ul>
  {/if}
</section>

<style>
  .head {
    display: flex;
    justify-content: space-between;
    align-items: baseline;
  }
  ul {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 6px;
  }
  li {
    display: grid;
    grid-template-columns: auto 1fr auto auto auto;
    align-items: center;
    gap: 10px;
    min-height: 44px;
  }
  .flags {
    display: flex;
    gap: 2px;
    color: var(--muted);
  }
  .pos {
    font-family: ui-monospace, 'SF Mono', Menlo, monospace;
    font-variant-numeric: tabular-nums;
    color: var(--muted);
  }
</style>
