// The grammatical model measured against the same pinned corpus the phrase lists are measured against.
// The point of the exercise is the comparison: a general mechanism only earns the right to replace an
// enumeration by covering everything the enumeration covered, and that is a number, not an opinion.
import { classify } from './deferral-shape.mjs';
import { CASES } from './deferral-corpus.mjs';

let pass = 0, fail = 0;
for (const [id, want, text] of CASES) {
  const hit = classify(text);
  const got = hit !== null;
  const ok = got === want;
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${id.padEnd(22)} expected ${want ? 'block' : 'quiet'}, got ${got ? 'block' : 'quiet'}`
    + (hit ? `  [${hit.why}]` : ''));
  if (!ok && hit) console.log(`       matched: ${hit.hit}`);
  ok ? pass++ : fail++;
}
console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
