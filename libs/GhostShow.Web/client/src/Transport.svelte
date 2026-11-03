<script lang="ts">
  // The editor's timeline on the show computer: what the desktop's transport works. Drag the bar to go somewhere.
  import Icon from './Icon.svelte';
  import { live } from './lib/live.svelte.ts';
  import { pathOf } from './lib/names.ts';
  import { formatTime } from './lib/show.ts';

  const stateLabel = { playing: 'Playing', paused: 'Paused', stopped: 'Stopped' } as const;

  /** Where the bar is being dragged to (shown instead of the playhead until let go). */
  let dragging = $state<number | null>(null);
  const focused = $derived(live.focused);
  const shown = $derived(dragging ?? focused?.position ?? 0);
  const playing = $derived(focused?.state === 'playing');
</script>

<section class="card">
  <h2>Editor's timeline</h2>
  {#if focused}
    <div class="now">
      <span class="state {focused.state}">{stateLabel[focused.state]}</span>
      <span class="name">{pathOf(live.show, focused.timeline)}</span>
      {#if focused.loop}<span class="chip">Loops</span>{/if}
    </div>
    <div class="time">
      <span>{formatTime(shown)}</span>{' '}<span class="total">/ {formatTime(focused.length)}</span>
    </div>
    <input
      class="scrub"
      type="range"
      min="0"
      max={Math.max(focused.length, 0.1)}
      step="0.1"
      value={shown}
      aria-label="Position"
      oninput={(e) => (dragging = Number(e.currentTarget.value))}
      onchange={async (e) => {
        await live.send({ action: 'seek', position: Number(e.currentTarget.value) });
        dragging = null;
      }}
    />
    <div class="controls">
      <button class="round" aria-label="Play from the start" title="Play from the start" onclick={() => live.send({ action: 'restart' })}>
        <Icon name="restart" />
      </button>
      <button class="round big" aria-label={playing ? 'Pause' : 'Play'} onclick={() => live.send({ action: playing ? 'pause' : 'play' })}>
        <Icon name={playing ? 'pause' : 'play'} size={30} />
      </button>
      <button class="round" aria-label="Stop" title="Stop" onclick={() => live.send({ action: 'stop' })}>
        <Icon name="stop" />
      </button>
    </div>
  {:else}
    <p class="muted">No timeline is open in the editor. Open one below.</p>
  {/if}
</section>

<style>
  .now {
    display: flex;
    align-items: center;
    gap: 10px;
    min-width: 0;
  }
  .chip {
    flex: none;
    font-size: 12px;
    padding: 2px 8px;
    border-radius: 999px;
    border: 1px solid var(--line);
    color: var(--muted);
  }
  .time {
    font-family: ui-monospace, 'SF Mono', Menlo, monospace;
    font-variant-numeric: tabular-nums;
    font-size: clamp(32px, 9vw, 48px);
    margin: 10px 0 4px;
  }
  .total {
    font-size: 0.45em;
    color: var(--muted);
    white-space: nowrap;
  }
  .scrub {
    width: 100%;
    height: 36px;
    margin: 0;
    accent-color: var(--accent);
    min-height: 0;
    padding: 0;
    border: 0;
    background: transparent;
  }
  .controls {
    display: flex;
    justify-content: center;
    align-items: center;
    gap: 20px;
    margin-top: 8px;
  }
  .round {
    width: 52px;
    height: 52px;
    border-radius: 50%;
    border: 1px solid var(--line);
    background: var(--raised);
    color: var(--text);
    display: grid;
    place-items: center;
  }
  .round.big {
    width: 68px;
    height: 68px;
    background: var(--accent);
    border: 0;
    color: #06141d;
  }
  .round:active {
    transform: scale(0.95);
  }
</style>
