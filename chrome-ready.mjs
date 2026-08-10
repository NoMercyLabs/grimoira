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
import { execFileSync, spawn } from 'node:child_process';
import { existsSync, mkdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';

// 9222 is the convention every CDP client defaults to, which is exactly why it
// is not always available: OBS's embedded browser claims it too, and on this
// machine it is running whenever the owner is streaming. The override is how the
// agent browser gets somewhere else to live without editing every driver.
const PORT = Number(process.env.NOMERCY_CDP_PORT || 9222);
const PROFILE = join(homedir(), '.claude', 'chrome-agent-profile');

const CHROME = [
  join(process.env.ProgramFiles || 'C:/Program Files', 'Google/Chrome/Application/chrome.exe'),
  join(process.env['ProgramFiles(x86)'] || 'C:/Program Files (x86)', 'Google/Chrome/Application/chrome.exe'),
  join(process.env.LOCALAPPDATA || '', 'Google/Chrome/Application/chrome.exe'),
];

// Who is answering on the port, not merely that somebody is.
//
// OBS embeds CEF and opens the SAME devtools protocol on 9222 when it is
// running with a browser source. It answers /json/version with a Chrome
// version string, so a check that only asks "does the port respond" reports
// the agent browser as up and every driver then attaches to OBS. That happened:
// a login driver navigated one of the owner's live browser sources to a IdP
// page while he was streaming.
//
// The User-Agent carries `OBS/<version>`, and the listening process is the
// ground truth behind it. Both are checked, because a future embedder will not
// say OBS.
async function occupant() {
  try {
    const c = new AbortController();
    const t = setTimeout(() => c.abort(), 1500);
    const r = await fetch(`http://127.0.0.1:${PORT}/json/version`, { signal: c.signal });
    clearTimeout(t);
    if (!r.ok) return null;
    const info = await r.json();
    return { agent: String(info['User-Agent'] ?? ''), process: owningProcess() };
  } catch { return null; }
}

// The process listening on PORT, by name. Empty when it cannot be determined,
// which is treated as "not proven foreign" rather than as a failure.
function owningProcess() {
  try {
    const lines = execFileSync('netstat', ['-ano'], { encoding: 'utf8' }).split('\n');
    const row = lines.find((line) => /LISTENING/.test(line) && new RegExp(`[:.]${PORT}\\b`).test(line));
    const pid = row?.trim().split(/\s+/).pop();
    if (!pid) return '';
    const tasks = execFileSync('tasklist', ['/FI', `PID eq ${pid}`, '/FO', 'CSV', '/NH'], { encoding: 'utf8' });
    return (tasks.split(',')[0] ?? '').replace(/"/g, '').replace(/\.exe$/i, '').toLowerCase();
  } catch { return ''; }
}

async function ensure() {
  const held = await occupant();
  if (held) {
    const foreign = /OBS\//i.test(held.agent)
      || (held.process && held.process !== 'chrome' && held.process !== 'chrome_proxy');
    if (foreign) {
      // Fails CLOSED, alone among the paths here. Every other failure lets the
      // tool run and report its own error; this one hands the caller a live
      // application belonging to somebody else, and "the driver did nothing" is
      // strictly better than "the driver drove OBS".
      return `port ${PORT} is held by ${held.process || 'another application'}, not the agent browser`;
    }
    return 'already up';
  }

  const exe = CHROME.find((p) => p && existsSync(p));
  if (!exe) return 'chrome not found';

  try { mkdirSync(PROFILE, { recursive: true }); } catch { /* exists */ }

  // Detached, with stdio ignored, so the browser outlives this hook process. Without unref() the hook
  // would sit waiting on a browser that stays open for the rest of the day.
  const child = spawn(exe, [
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${PROFILE}`,
    // A normal desktop window, not the tall narrow default the fresh profile opens with. Sets the
    // real OS window size at launch; the inner viewport follows it, never a metrics override.
    '--window-size=1280,800',
    '--window-position=60,60',
    '--restore-last-session',
    '--no-first-run',
    '--no-default-browser-check',
  ], { detached: true, stdio: 'ignore' });
  child.unref();

  // Chrome writes the endpoint before it finishes painting, so this normally settles in about a second.
  for (let i = 0; i < 25; i++) {
    await new Promise((r) => setTimeout(r, 400));
    if (await occupant()) return 'started';
  }
  return 'started but the port never opened';
}

// Collapse DUPLICATE tabs in the agent browser. Every tool that "opens" a page (new_page, or a driver
// calling Target.createTarget) leaves the previous one behind, so ordinary navigation walked the window
// to nine tabs of the same route while the owner watched — it is his screen.
//
// Deliberately conservative, because more than one session can share this browser: only tabs whose URL
// is an EXACT duplicate of one already kept are closed, the most-recently-active copy survives, and a
// unique page is never touched. Capping to a flat N is the wrong instrument here — it would close the
// page another session is driving, and closing the last window makes Chrome exit, which is how a live
// browser turned into a bare newtab. There is always at least one tab left.
//
// Best-effort: a prune failure must never block the browser call behind it.
async function pruneDuplicateTabs() {
  try {
    const res = await fetch(`http://127.0.0.1:${PORT}/json`);
    if (!res.ok) return 0;
    const pages = (await res.json()).filter((t) => t.type === 'page' && t.id);
    if (pages.length < 2) return 0;

    // DevTools lists most-recently-active first, so the first copy of a URL is the one to keep.
    const seen = new Set();
    const doomed = [];
    for (const p of pages) {
      const key = (p.url || '').split('#')[0];
      if (!key || key === 'about:blank') continue;   // a blank tab is not a duplicate of anything
      if (seen.has(key)) doomed.push(p);
      else seen.add(key);
    }

    let closed = 0;
    for (const p of doomed) {
      if (pages.length - closed <= 1) break;         // never take the last tab; Chrome would exit
      try { await fetch(`http://127.0.0.1:${PORT}/json/close/${p.id}`); closed++; } catch { /* gone already */ }
    }
    return closed;
  } catch { return 0; }
}

if (process.argv.includes('--ensure')) {
  const state = await ensure();
  console.log(`agent chrome: ${state} (port ${PORT}, profile ${PROFILE})`);
  if (process.argv.includes('--prune')) console.log(`pruned ${await pruneDuplicateTabs()} duplicate tab(s)`);
  process.exit(state === 'chrome not found' ? 1 : 0);
}

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', async () => {
  try {
    const state = await ensure();
    // Collapse any tab pile-up BEFORE the call runs, so the tool acts on the one surviving tab.
    const pruned = state === 'started' ? 0 : await pruneDuplicateTabs();
    const notes = [];
    if (state === 'started') notes.push(`agent chrome was not running, started it on ${PORT}`);
    if (pruned > 0) notes.push(`closed ${pruned} duplicate tab(s)`);
    if (notes.length) {
      // Worth one line, because a browser appearing unbidden (or tabs vanishing) should be explicable.
      process.stdout.write(JSON.stringify({ systemMessage: `aitm: ${notes.join('; ')}.` }));
    }
  } catch {
    // fail open — never block a browser call because the readiness check itself broke
  }
  process.exit(0);
});
