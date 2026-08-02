// The fingerprints have to be sharp in both directions: a guard that cries wolf on every locale entry
// gets muted, and a muted guard is worth less than no guard. Each case here is a real shape from this
// repo or from a session where the patch went in one place and the siblings stayed broken.
import { skeleton, removedTokens, addedLines, countSkeleton, analyse } from './blast-radius.mjs';

let pass = 0, fail = 0;
const check = (ok, label, detail) => {
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${label}`);
  if (!ok && detail) console.log(`       ${detail}`);
  ok ? pass++ : fail++;
};

// --- skeleton: same construct, different contents, one shape.
const a = skeleton('const TEST_NAMED = new RegExp([');
const b = skeleton('const PROPOSAL = new RegExp([');
check(a !== null && a === b, 'two regex constants share one skeleton', `${a} vs ${b}`);

check(skeleton('if (foo === bar) {') === skeleton('if (baz === qux) {'),
  'two branches of an if-chain share one skeleton');

check(skeleton('}') === null && skeleton('  ),') === null && skeleton('') === null,
  'punctuation-only lines are not a pattern');

check(skeleton('let x = 1') === null,
  'a trivially short line is not a pattern', String(skeleton('let x = 1')));

check(skeleton("  'still open\\\\b', 'still outstanding',") === skeleton("  'worth a look', 'on your side',"),
  'two rows of a phrase list share one skeleton');

// --- removedTokens: what the edit took away is what may be unfixed elsewhere.
check(removedTokens('JsonSerializer.Serialize(payload)', 'writer.WriteString(payload)')
  .includes('JsonSerializer.Serialize'), 'a replaced dotted call is a removed token');

check(removedTokens('const x = 1;', 'const x = 2;').length === 0,
  'common language words carry no blast radius');

check(removedTokens('foo(bar)', 'foo(bar, baz)').length === 0,
  'a pure addition removes nothing');

check(removedTokens('setAutoCancel(true)', 'setDeleteIntent(pi)').includes('setAutoCancel'),
  'a swapped API name is a removed token');

// The first live firing of this guard, in the same session it shipped: rewording a message string
// accused the English word "matched" of being an unfixed construct in seven other files.
check(removedTokens(
  '`You wrote up work you can do instead of doing it (matched: "${hit}"). Diagnosing a problem`',
  '`You wrote up work you can do instead of doing it — ${hit.why}. Diagnosing a problem`',
).length === 0, 'prose inside a template literal is not a construct');

check(removedTokens('// call oldHelper here\nfoo();', 'foo();').length === 0,
  'an identifier mentioned only in a comment is not a construct');

check(removedTokens('const u = "https://old.example.com/x"; callOldHelper(u);', 'const u = URL; callNewHelper(u);')
  .includes('callOldHelper'), 'a real identifier still survives the literal stripping');

// --- addedLines / countSkeleton
check(addedLines('a\nb', 'a\nb\nc').join() === 'c', 'added lines are the ones not already there');
const six = Array.from({ length: 6 }, (_, i) => `const RULE_${i} = new RegExp([`).join('\n');
check(countSkeleton(six, skeleton('const RULE_9 = new RegExp([')) === 6, 'six siblings counted');

// --- analyse: enumeration growth
const enumPayload = {
  tool_input: {
    file_path: 'C:/Projects/aitm/continue-guard.mjs',
    old_string: 'const A = new RegExp([',
    new_string: 'const A = new RegExp([\nconst RULE_9 = new RegExp([',
  },
};
const enumNotes = analyse(enumPayload, { readFile: () => six, search: () => [] });
check(enumNotes.some((n) => n.kind === 'enumeration'), 'the fifth arm on a list of arms fires');

// The same edit in a test table must stay silent — those lists are meant to grow.
const testFileNotes = analyse(
  { tool_input: { ...enumPayload.tool_input, file_path: 'C:/Projects/aitm/continue-guard.test.mjs' } },
  { readFile: () => six, search: () => [] },
);
check(!testFileNotes.some((n) => n.kind === 'enumeration'), 'a test table is exempt from enumeration growth');

const localeNotes = analyse(
  { tool_input: { ...enumPayload.tool_input, file_path: 'C:/Projects/app/src/locales/en.js' } },
  { readFile: () => six, search: () => [] },
);
check(!localeNotes.some((n) => n.kind === 'enumeration'), 'a locale file is exempt too');

// --- analyse: unfixed siblings
const sibNotes = analyse(
  {
    tool_input: {
      file_path: 'C:/Projects/app/src/Notify.cs',
      old_string: 'builder.setAutoCancel(true);',
      new_string: 'builder.setDeleteIntent(cancelPi);',
    },
  },
  { readFile: () => '', search: () => ['C:/Projects/app/src/Other.cs', 'C:/Projects/app/src/Third.cs'] },
);
check(sibNotes.some((n) => n.kind === 'siblings' && /2 other file/.test(n.text)),
  'a construct removed here and left in two other files fires');

const cleanNotes = analyse(
  {
    tool_input: {
      file_path: 'C:/Projects/app/src/Notify.cs',
      old_string: 'builder.setAutoCancel(true);',
      new_string: 'builder.setDeleteIntent(cancelPi);',
    },
  },
  { readFile: () => '', search: () => [] },
);
check(cleanNotes.length === 0, 'the only occurrence in the repo fires nothing');

console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
