// PreToolUse on the browser tools: make sure there is a browser to talk to.
//
// The chrome-attached MCP server attaches to a debugging port instead of launching its own blank
// profile, which is what stopped every automated page from landing on a login wall. The cost of that
// choice is a precondition: if the port is not listening, every browser tool fails hard, and a session
// dies mid-task over a browser nobody remembered to start.
//
// Leaving that as "run the script first" was the defect. A precondition a human has to remember is a
// precondition that gets forgotten, and the person who pays is whoever is mid-sentence when it breaks.
// So it is checked here, on the way to the tool, and started if missing.
//
// Fast path is a single loopback request. Fails OPEN — if the browser cannot be started the tool still
// runs and reports its own error, because a guard must never be the reason something is impossible.
//
//   node chrome-ready.mjs              as a hook, reads the payload on stdin
//   node chrome-ready.mjs --ensure     as a command, for a manual start
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';

const PORT = 9222;
const PROFILE = join(homedir(), '.claude', 'chrome-agent-profile');

const CHROME = [
  join(process.env.ProgramFiles || 'C:/Program Files', 'Google/Chrome/Application/chrome.exe'),
  join(process.env['ProgramFiles(x86)'] || 'C:/Program Files (x86)', 'Google/Chrome/Application/chrome.exe'),
  join(process.env.LOCALAPPDATA || '', 'Google/Chrome/Application/chrome.exe'),
];

async function listening() {
  try {
    const c = new AbortController();
    const t = setTimeout(() => c.abort(), 1500);
    const r = await fetch(`http://127.0.0.1:${PORT}/json/version`, { signal: c.signal });
    clearTimeout(t);
    return r.ok;
  } catch { return false; }
}

async function ensure() {
  if (await listening()) return 'already up';

  const exe = CHROME.find((p) => p && existsSync(p));
  if (!exe) return 'chrome not found';

  try { mkdirSync(PROFILE, { recursive: true }); } catch { /* exists */ }

  // Detached, with stdio ignored, so the browser outlives this hook process. Without unref() the hook
  // would sit waiting on a browser that stays open for the rest of the day.
  const child = spawn(exe, [
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${PROFILE}`,
    '--restore-last-session',
    '--no-first-run',
    '--no-default-browser-check',
  ], { detached: true, stdio: 'ignore' });
  child.unref();

  // Chrome writes the endpoint before it finishes painting, so this normally settles in about a second.
  for (let i = 0; i < 25; i++) {
    await new Promise((r) => setTimeout(r, 400));
    if (await listening()) return 'started';
  }
  return 'started but the port never opened';
}

if (process.argv.includes('--ensure')) {
  const state = await ensure();
  console.log(`agent chrome: ${state} (port ${PORT}, profile ${PROFILE})`);
  process.exit(state === 'chrome not found' ? 1 : 0);
}

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', async () => {
  try {
    const state = await ensure();
    if (state === 'started') {
      // Worth one line, because a browser appearing unbidden should be explicable.
      process.stdout.write(JSON.stringify({
        systemMessage: `aitm: agent chrome was not running, started it on ${PORT} before the browser call.`,
      }));
    }
  } catch {
    // fail open — never block a browser call because the readiness check itself broke
  }
  process.exit(0);
});
