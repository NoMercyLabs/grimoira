// Shared guard for any test that opens or writes an AITM store. A real, populated instance (e.g.
// "nomercy") holds live facts, memory and history; a test must never resolve to one, even as a side
// effect of a hook it drives end to end. Every test instance name in this repo already contains "test"
// or "fixture" (agreement-test, graphtest, stagetest, workspace-tool-test, gatefixture, ...) — anything
// else is treated as real and refused before the first write.
const SAFE = /test|fixture/i;

export function assertTestInstance(instance) {
  if (typeof instance !== 'string' || !SAFE.test(instance)) {
    throw new Error(`refusing to use instance "${instance}" in a test: name must contain "test" or "fixture"`);
  }
}
