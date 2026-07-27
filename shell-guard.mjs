// PreToolUse hook on Bash|PowerShell: kills the Windows path-shape mistakes that get made over and
// over on this machine. The shells and the binaries disagree about what a path looks like, and being
// careful is not a fix for a disagreement that recurs on every command.
//
// Three failures, handled by kind:
//   1. MSYS paths (/c/..., ~/..., $HOME/...) handed to a Windows binary, which cannot resolve them.
//      Mechanically fixable, so it is rewritten in place rather than refused.
//   2. Backslash Windows paths inside a bash command, where \P and \a are escape sequences and the
//      path silently collapses to something drive-relative.
//   3. A long inline `node -e` script, where the quoting breaks between bash, PowerShell and node.
//      Not mechanically fixable; refused with the approach that does work.
//
// Fails open on any error.
import { homedir } from 'node:os';

const HOME = homedir().replace(/\\/g, '/');
// Binaries that parse their own path arguments and have no idea what /c/ means.
const WIN_BIN = /(^|[\s;&|(])(node|npx|dotnet|pwsh|powershell|py|python|java|gradlew?|[^\s]+\.exe)\b/i;

const allow = () => process.exit(0);

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', () => {
  try {
    const payload = JSON.parse(input || '{}');
    const cmd = payload.tool_input?.command;
    if (typeof cmd !== 'string' || cmd.length === 0) allow();
    const isPwsh = payload.tool_name === 'PowerShell';

    // 3) Inline script that has to survive two levels of quoting. Refuse and redirect.
    const inlineEval = cmd.match(/\bnode\s+(-e|--eval)\s+(["'])([\s\S]*?)\2/);
    if (inlineEval && inlineEval[3].length > 120) {
      process.stdout.write(JSON.stringify({
        hookSpecificOutput: {
          hookEventName: 'PreToolUse',
          permissionDecision: 'deny',
          permissionDecisionReason:
            `This is a ${inlineEval[3].length}-character inline "node -e" script. Its quotes have to survive ` +
            `the shell AND node, and on this machine that reliably breaks — it has cost several failed calls ` +
            `already. Write the script to a .mjs file in the scratchpad with the Write tool and run it by path. ` +
            `Import shared helpers as file:///C:/Projects/aitm/brain-lib.mjs (a bare C:/ path is not a legal ESM ` +
            `specifier on Windows).`,
        },
      }));
      process.exit(0);
    }

    if (!WIN_BIN.test(cmd)) allow();

    // 1) + 2) Mechanically fixable path shapes.
    let fixed = cmd;
    const notes = [];

    const before1 = fixed;
    fixed = fixed.replace(/(^|\s)~\//g, `$1${HOME}/`).replace(/\$HOME\//g, `${HOME}/`);
    if (fixed !== before1) notes.push('~ and $HOME expanded to a real Windows path');

    const before2 = fixed;
    fixed = fixed.replace(/(^|[\s"'=(])\/([a-zA-Z])\//g, (_m, lead, drive) => `${lead}${drive.toUpperCase()}:/`);
    if (fixed !== before2) notes.push('MSYS /c/ style paths rewritten to C:/');

    // Per token, not per match: a single global replace only converted the first backslash run and
    // left "C:/Projects\aitm/x.mjs", which is still broken and harder to spot than the original.
    if (!isPwsh && /[A-Za-z]:\\/.test(fixed)) {
      const before3 = fixed;
      fixed = fixed.split(/(\s+)/).map((tok) =>
        /[A-Za-z]:\\/.test(tok) ? tok.replace(/\\+/g, '/') : tok
      ).join('');
      if (fixed !== before3) notes.push('backslash paths rewritten to forward slashes (bash eats the backslash)');
    }

    if (fixed === cmd) allow();

    process.stdout.write(JSON.stringify({
      hookSpecificOutput: {
        hookEventName: 'PreToolUse',
        permissionDecision: 'allow',
        permissionDecisionReason: `aitm shell-guard: ${notes.join('; ')}.`,
        updatedInput: { ...payload.tool_input, command: fixed },
      },
    }));
  } catch {
    // fail open — a guard error must never block a real command
  }
  process.exit(0);
});
