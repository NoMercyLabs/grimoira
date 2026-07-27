// UserPromptSubmit hook: the store consults itself on every prompt, no tool call needed. Ranking and
// channel handling live in brain-lib so this stays in step with the gate and the track record — the
// hand-rolled copy that used to live here truncated candidates with an unranked LIMIT and blanked the
// memory hook field, so it was quietly answering with its second-best material on every turn.
//
// Strict noise gates: multi-token coverage required, tiny output, silent on miss, always fails open.
import { resolveInstance, openRead, tokenize, search, format, logGap, openWrite } from './brain-lib.mjs';

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', async () => {
  let db = null;
  try {
    const payload = JSON.parse(input || '{}');
    const prompt = (payload.prompt || '').trim();
    if (prompt.length < 12 || prompt.startsWith('/')) process.exit(0);

    const tokens = tokenize(prompt);
    if (tokens.length < 2) process.exit(0);

    const instance = resolveInstance(payload);
    db = await openRead(instance);
    if (!db) process.exit(0);
    const picks = search(db, tokens, { limit: 4 });
    db.close();
    db = null;

    if (picks.length === 0) {
      // A prompt the store cannot speak to is a gap worth knowing about, same as a search it missed.
      const w = await openWrite(instance);
      if (w) { logGap(w, 'prompt', [...tokens].sort().join(' ')); w.close(); }
      process.exit(0);
    }

    process.stdout.write(JSON.stringify({
      hookSpecificOutput: {
        hookEventName: 'UserPromptSubmit',
        // The caveat is per row, not a banner. A blanket "verify before relying on it" told the model to
        // go and check EVERYTHING, so a probe that was handed the package's home and version at rank 2
        // opened package.json to confirm it — the store answered and got overruled by its own header.
        // A hard rule and a stated fact carry authority; only a guess needs checking.
        additionalContext: `aitm recall (auto):\n${format(picks, { caveat: true })}`,
      },
    }));
  } catch {
    // fail open — never block a prompt on a recall error
  } finally {
    try { if (db) db.close(); } catch { /* already closed */ }
  }
  process.exit(0);
});
