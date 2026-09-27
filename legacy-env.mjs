// Compat shim (slice 32c): AITM_* env vars still count for one release. An empty GRIMORA_X counts as unset.
// The store move lives in the server start only (LegacyStore.cs).
export function promoteLegacyEnv(env = process.env) {
  for (const [name, value] of Object.entries(env)) {
    if (!/^AITM_/i.test(name)) continue;
    const twin = `GRIMORA_${name.slice(5)}`;
    if (!env[twin]) env[twin] = value;
  }
}
