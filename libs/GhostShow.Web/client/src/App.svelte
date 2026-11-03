<script lang="ts">
  import { live } from './lib/live.svelte.ts';
  import Pair from './Pair.svelte';
  import Show from './Show.svelte';

  live.connect();
</script>

{#if live.paired}
  <Show />
{:else}
  <Pair />
{/if}
