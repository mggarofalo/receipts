import { openSync, writeFileSync, closeSync, unlinkSync, existsSync, readlinkSync } from 'node:fs';
import { hostname } from 'node:os';
import { join } from 'node:path';

// Never steal on age. An API may die while its provider child is still alive.
export function acquireLifetimeLock(home, permit) {
  const path = join(home, '.node.lock');
  let fd;
  try { fd = openSync(path, 'wx', 0o600); }
  catch { throw new Error('busy'); }
  try {
    const namespace = process.platform === 'linux' ? readlinkSync('/proc/self/ns/pid') : 'host';
    writeFileSync(fd, `${hostname()}|${namespace}|${process.pid}`);
    // Recovery removes old permits while holding this same lifetime guard.
    // A delayed orphan therefore cannot begin after recovery released the guard.
    if (!/^[a-f0-9]{32}$/.test(permit ?? '') || !existsSync(join(home, '.permit-' + permit))) throw new Error('busy');
  } catch {
    closeSync(fd); unlinkSync(path); throw new Error('busy');
  }
  return () => { closeSync(fd); unlinkSync(path); };
}
