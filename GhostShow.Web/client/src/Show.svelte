<script lang="ts">
  // The live show: Blackout always at the top. An editor gets the transport, buttons, what's playing and every
  // timeline; an operator (a phone at the entrance) gets big buttons, what's playing, and the show's health.
  import Buttons from './Buttons.svelte';
  import { live } from './lib/live.svelte.ts';
  import Playing from './Playing.svelte';
  import Timelines from './Timelines.svelte';
  import Transport from './Transport.svelte';
</script>

<header class="top">
  <div class="title">
    <h1>{live.show?.name ?? 'GhostShow'}</h1>
    <span class="link" class:up={live.connected} role="status">
      <span class="dot" aria-hidden="true"></span>{live.connected ? (live.device ? `${live.device.name} · ${live.isEditor ? 'Editor' : 'Operator'}` : 'Connected') : 'Offline'}
    </span>
  </div>
  <div class="actions">
    {#if live.fullApp && live.isEditor}
      <a class="full" href="/app/" title="The whole app: timelines, envelopes, editing">Open the full app</a>
    {/if}
    <button class="blackout" onclick={() => live.send({ action: 'blackout' })} title="Stop playback and set every channel to 0">Blackout</button>
  </div>
</header>

<main class="show" class:editor={live.isEditor}>
  {#if !live.connected}
    <p class="notice wide">{live.problem ?? 'Connecting to the show computer…'}</p>
  {/if}

  {#if live.warnings.length > 0}
    <section class="card warnings wide" role="alert">
      <h2>Needs attention</h2>
      <ul>
        {#each live.warnings as warning, i (i)}<li>{warning}</li>{/each}
      </ul>
    </section>
  {/if}

  {#if live.show}
    {#if live.isEditor}
      <div class="column">
        <Transport />
        <Buttons />
      </div>
      <div class="column">
        <Playing />
        <Timelines />
      </div>
    {:else}
      <div class="column">
        <Buttons big />
        <Playing />
        {#if live.warnings.length === 0}
          <p class="muted ok">✓ Everything's working on the show computer.</p>
        {/if}
      </div>
    {/if}
  {/if}

  <footer class="wide">
    <span class="muted small">{live.lastChange ? `Last change: ${live.lastChange}` : ''}</span>
    <button class="quiet" onclick={() => live.unpair()}>Unpair this device</button>
  </footer>
</main>

{#if live.toast}
  <div class="toast" role="status">{live.toast}</div>
{/if}

<style>
  .top {
    position: sticky;
    top: 0;
    z-index: 2;
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 12px;
    padding: max(10px, env(safe-area-inset-top)) 16px 10px;
    background: color-mix(in srgb, var(--bg) 92%, transparent);
    backdrop-filter: blur(8px);
    border-bottom: 1px solid var(--line);
  }
  .title {
    min-width: 0;
  }
  h1 {
    margin: 0;
    font-size: 20px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .link {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-size: 12px;
    color: var(--muted);
  }
  .dot {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: var(--warn);
  }
  .link.up .dot {
    background: var(--ok);
  }
  .actions {
    display: flex;
    align-items: center;
    gap: 10px;
    flex: none;
  }
  .full {
    color: var(--accent);
    font-weight: 600;
    text-decoration: none;
    padding: 12px 4px;
    white-space: nowrap;
  }
  .blackout {
    flex: none;
    min-height: 48px;
    padding: 0 20px;
    border: 0;
    border-radius: 10px;
    background: var(--danger);
    color: #fff;
    font-weight: 800;
    font-size: 16px;
    letter-spacing: 0.02em;
  }
  .blackout:active {
    transform: scale(0.97);
  }
  .show {
    max-width: 1100px;
    margin: 0 auto;
    padding: 14px 16px 32px;
    display: grid;
    gap: 14px;
    grid-template-columns: minmax(0, 1fr);
  }
  .column {
    display: grid;
    gap: 14px;
    align-content: start;
    min-width: 0;
  }
  @media (min-width: 900px) {
    .show.editor {
      grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
    }
    .show.editor .wide {
      grid-column: 1 / -1;
    }
  }
  .notice {
    margin: 0;
    background: var(--panel);
    border-left: 3px solid var(--warn);
    padding: 10px 12px;
    border-radius: 6px;
  }
  .warnings {
    border-color: var(--warn);
  }
  .warnings ul {
    margin: 0;
    padding-left: 18px;
    display: grid;
    gap: 4px;
  }
  .ok {
    margin: 0;
    text-align: center;
  }
  footer {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 12px;
    flex-wrap: wrap;
  }
  .small {
    font-size: 13px;
  }
  .toast {
    position: fixed;
    left: 50%;
    bottom: max(20px, env(safe-area-inset-bottom));
    transform: translateX(-50%);
    width: min(520px, calc(100% - 32px));
    background: #2a1b1b;
    border: 1px solid var(--danger);
    color: var(--text);
    padding: 12px 14px;
    border-radius: 10px;
    box-shadow: 0 8px 30px rgb(0 0 0 / 50%);
    z-index: 3;
  }
</style>
