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
// A hook process does NOT inherit the shell env, so reading NOMERCY_CDP_PORT and stopping there made
// every hook invocation look at 9222, find nothing, and try to LAUNCH a browser — while the agent
// browser it was supposed to tidy sat on 9333 collecting 21 copies of the same page. The port is
// discovered across the candidates instead of assumed, and only a port with no agent browser anywhere
// is launched on.
const CANDIDATE_PORTS = [
  ...(process.env.NOMERCY_CDP_PORT ? [Number(process.env.NOMERCY_CDP_PORT)] : []),
  9333,
  9222,
].filter((p, i, all) => Number.isFinite(p) && all.indexOf(p) === i);
const PORT = CANDIDATE_PORTS[0];
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
async function occupant(port = PORT) {
  try {
    const c = new AbortController();
    const t = setTimeout(() => c.abort(), 1500);
    const r = await fetch(`http://127.0.0.1:${port}/json/version`, { signal: c.signal });
    clearTimeout(t);
    if (!r.ok) return null;
    const info = await r.json();
    return { port, agent: String(info['User-Agent'] ?? ''), process: owningProcess(port) };
  } catch { return null; }
}

// True when the thing answering on this port is our own Chrome and not somebody else's embedded CEF.
function isAgentBrowser(held) {
  if (!held) return false;
  if (/OBS\//i.test(held.agent)) return false;
  return !held.process || held.process === 'chrome' || held.process === 'chrome_proxy';
}

// The first candidate port answering as a real agent browser, or null when none is up.
async function findAgentBrowser() {
  for (const port of CANDIDATE_PORTS) {
    const held = await occupant(port);
    if (isAgentBrowser(held)) return held;
  }
  return null;
}

// The process listening on PORT, by name. Empty when it cannot be determined,
// which is treated as "not proven foreign" rather than as a failure.
function owningProcess(port = PORT) {
  try {
    const lines = execFileSync('netstat', ['-ano'], { encoding: 'utf8' }).split('\n');
    const row = lines.find((line) => /LISTENING/.test(line) && new RegExp(`[:.]${port}\\b`).test(line));
    const pid = row?.trim().split(/\s+/).pop();
    if (!pid) return '';
    const tasks = execFileSync('tasklist', ['/FI', `PID eq ${pid}`, '/FO', 'CSV', '/NH'], { encoding: 'utf8' });
    return (tasks.split(',')[0] ?? '').replace(/"/g, '').replace(/\.exe$/i, '').toLowerCase();
  } catch { return ''; }
}

async function ensure() {
  const live = await findAgentBrowser();
  if (live) return { state: 'already up', port: live.port };

  // Nothing answered as ours. If the preferred port is nonetheless occupied, that is somebody else's
  // application and we fail CLOSED — alone among the paths here. Every other failure lets the tool run
  // and report its own error; this one would hand the caller a live application belonging to somebody
  // else, and "the driver did nothing" is strictly better than "the driver drove OBS".
  const squatter = await occupant(PORT);
  if (squatter) {
    return { state: `port ${PORT} is held by ${squatter.process || 'another application'}, not the agent browser`, port: PORT };
  }

  const exe = CHROME.find((p) => p && existsSync(p));
  if (!exe) return { state: 'chrome not found', port: PORT };

  try { mkdirSync(PROFILE, { recursive: true }); } catch { /* exists */ }

  // Detached, with stdio ignored, so the browser outlives this hook process. Without unref() the hook
  // would sit waiting on a browser that stays open for the rest of the day.
  const child = spawn(exe, [
    `--remote-debugging-port=${PORT}`,
    // Explicit, because on this machine Chrome's own default binds the
    // devtools port to ::1 only — every caller here (and every driver in
    // this directory) dials 127.0.0.1, so an IPv6-only bind reads as the
    // port never opening even though Chrome is listening fine on ::1.
    '--remote-debugging-address=127.0.0.1',
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
    if (await occupant(PORT)) return { state: 'started', port: PORT };
  }
  return { state: 'started but the port never opened', port: PORT };
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
async function pruneDuplicateTabs(port = PORT) {
  try {
    const res = await fetch(`http://127.0.0.1:${port}/json`);
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
      try { await fetch(`http://127.0.0.1:${port}/json/close/${p.id}`); closed++; } catch { /* gone already */ }
    }
    return closed;
  } catch { return 0; }
}

if (process.argv.includes('--ensure')) {
  const { state, port } = await ensure();
  console.log(`agent chrome: ${state} (port ${port}, profile ${PROFILE})`);
  if (process.argv.includes('--prune')) console.log(`pruned ${await pruneDuplicateTabs(port)} duplicate tab(s)`);
  process.exit(state === 'chrome not found' ? 1 : 0);
}

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', async () => {
  try {
    const { state, port } = await ensure();
    // Collapse any tab pile-up BEFORE the call runs, so the tool acts on the one surviving tab.
    const pruned = state === 'started' ? 0 : await pruneDuplicateTabs(port);
    const notes = [];
    if (state === 'started') notes.push(`agent chrome was not running, started it on ${port}`);
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
