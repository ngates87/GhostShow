<script lang="ts">
  import { icons } from './lib/icons.ts';

  let { name, size = 22 }: { name: keyof typeof icons; size?: number } = $props();
</script>

<svg width={size} height={size} viewBox="0 0 24 24" aria-hidden="true" fill="currentColor"><path d={icons[name]} /></svg>
