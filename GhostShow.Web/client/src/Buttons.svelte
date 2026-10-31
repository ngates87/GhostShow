<script lang="ts">
  // The show's buttons, as on the desktop's Buttons panel: tap to press (run their actions). A button is lit while a
  // timeline it runs is playing.
  import { keyLabel } from './lib/icons.ts';
  import { live } from './lib/live.svelte.ts';

  interface ButtonModel {
    id: string;
    name: string;
    color?: string | null;
    key?: string | null;
    actions: { type: string; timelineId?: string }[];
  }

  let { big = false }: { big?: boolean } = $props();

  const buttons = $derived((live.show?.buttons ?? []) as unknown as ButtonModel[]);
  const running = $derived(new Set(live.running.filter((r) => r.state !== 'stopped').map((r) => r.timeline.toLowerCase())));

  function lit(button: ButtonModel): boolean {
    return button.actions.some((a) => a.type === 'runTimeline' && a.timelineId !== undefined && running.has(a.timelineId.toLowerCase()));
  }

  let pressed = $state<string | null>(null);

  async function press(button: ButtonModel) {
    pressed = button.id;
    await live.send({ action: 'press', button: button.id });
    setTimeout(() => {
      if (pressed === button.id) pressed = null;
    }, 150);
  }
</script>

<section class="card">
  <h2>Buttons</h2>
  {#if buttons.length === 0}
    <p class="muted">This show has no buttons. Add them on the show computer (the Buttons panel beside the timeline).</p>
  {:else}
    <div class="grid" class:big>
      {#each buttons as button (button.id)}
        <button
          class="tile"
          class:lit={lit(button)}
          class:down={pressed === button.id}
          style:--tile={button.color ?? '#3b6ea5'}
          onclick={() => press(button)}
        >
          <span class="label">{button.name}</span>
          {#if button.key}<span class="key">{keyLabel(button.key)}</span>{/if}
        </button>
      {/each}
    </div>
  {/if}
</section>

<style>
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
    gap: 10px;
  }
  .grid.big {
    grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
  }
  .tile {
    position: relative;
    min-height: 72px;
    border: 0;
    border-radius: 12px;
    background: var(--tile);
    color: #fff;
    font-weight: 700;
    font-size: 16px;
    padding: 12px;
    text-align: left;
    display: flex;
    align-items: flex-end;
    text-shadow: 0 1px 2px rgb(0 0 0 / 55%);
    box-shadow: inset 0 0 0 1px rgb(255 255 255 / 12%);
    transition: transform 80ms;
  }
  .big .tile {
    min-height: 96px;
    font-size: 18px;
  }
  .tile.lit {
    box-shadow: 0 0 0 3px var(--lit), 0 0 18px var(--lit);
  }
  .tile.down {
    transform: scale(0.96);
  }
  .label {
    overflow-wrap: anywhere;
  }
  .key {
    position: absolute;
    top: 8px;
    right: 10px;
    font-size: 12px;
    font-weight: 600;
    opacity: 0.85;
  }
</style>
