<script lang="ts">
  import { guessDeviceName, live, type Role } from './lib/live.svelte.ts';

  let pin = $state('');
  let name = $state(guessDeviceName());
  let role = $state<Role>('editor');
  let busy = $state(false);
  let error = $state<string | null>(null);

  const ready = $derived(/^\d{6}$/.test(pin) && name.trim().length > 0 && !busy);

  async function pair(event: SubmitEvent) {
    event.preventDefault();
    if (!ready) return;
    busy = true;
    error = await live.pair(pin, name, role);
    busy = false;
  }
</script>

<main class="pair">
  <header>
    <h1>GhostShow</h1>
    <p class="muted">Pair this device with the show computer. You only do this once.</p>
  </header>

  {#if live.problem}
    <p class="notice">{live.problem}</p>
  {/if}

  <form onsubmit={pair}>
    <label>
      <span>PIN</span>
      <input
        class="pin"
        bind:value={pin}
        inputmode="numeric"
        autocomplete="one-time-code"
        maxlength="6"
        placeholder="••••••"
        aria-describedby="pin-help"
      />
      <small id="pin-help" class="muted">On the show computer: View → Remote Access.</small>
    </label>

    <label>
      <span>This device's name</span>
      <input bind:value={name} maxlength="60" autocomplete="off" />
    </label>

    <fieldset>
      <legend>What it's for</legend>
      <div class="choice">
        <button type="button" class:on={role === 'editor'} aria-pressed={role === 'editor'} onclick={() => (role = 'editor')}>
          <strong>Editor</strong>
          <small>Run, tweak and edit the show</small>
        </button>
        <button type="button" class:on={role === 'operator'} aria-pressed={role === 'operator'} onclick={() => (role = 'operator')}>
          <strong>Operator</strong>
          <small>Start and stop it, see how it's doing</small>
        </button>
      </div>
    </fieldset>

    {#if error}
      <p class="error" role="alert">{error}</p>
    {/if}

    <button class="primary" type="submit" disabled={!ready}>{busy ? 'Pairing…' : 'Pair'}</button>
  </form>
</main>

<style>
  .pair {
    max-width: 440px;
    margin: 0 auto;
    padding: 32px 16px;
  }
  h1 {
    margin: 0 0 4px;
    font-size: 28px;
  }
  form {
    display: grid;
    gap: 20px;
    margin-top: 24px;
  }
  label,
  fieldset {
    display: grid;
    gap: 6px;
    border: 0;
    padding: 0;
    margin: 0;
  }
  label > span,
  legend {
    font-weight: 600;
    padding: 0;
    margin-bottom: 6px;
  }
  .pin {
    font-size: 32px;
    letter-spacing: 0.4em;
    text-align: center;
    font-variant-numeric: tabular-nums;
  }
  .choice {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 10px;
  }
  .choice button {
    display: grid;
    gap: 4px;
    text-align: left;
    padding: 14px;
    border-radius: 12px;
    border: 1px solid var(--line);
    background: var(--panel);
    color: var(--text);
  }
  .choice button.on {
    border-color: var(--accent);
    box-shadow: 0 0 0 1px var(--accent) inset;
  }
  .choice small {
    color: var(--muted);
  }
  .notice {
    background: var(--panel);
    border-left: 3px solid var(--warn);
    padding: 10px 12px;
    border-radius: 6px;
  }
  .error {
    color: var(--danger-text);
    margin: 0;
  }
</style>
