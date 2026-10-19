import { join } from 'node:path';
import { classifyError } from './errors.mjs';
import { runCommand } from './protocol.mjs';

process.umask(0o077);
const command = process.argv[2];
const emit = message => process.stdout.write(JSON.stringify(message) + '\n');
try {
  if (!process.env.RECEIPTS_CHATGPT_HOME) throw new Error('auth_required');
  const { ModelRuntime } = await import('@earendil-works/pi-coding-agent');
  const runtime = await ModelRuntime.create({
    authPath: join(process.env.RECEIPTS_CHATGPT_HOME, 'auth.json'),
    modelsPath: null,
    modelsStorePath: join(process.env.RECEIPTS_CHATGPT_HOME, 'models-cache.json'),
    allowModelNetwork: false,
    refreshOnCreate: false,
  });
  let request;
  if (command === 'infer') {
    const chunks = [];
    let bytes = 0;
    for await (const chunk of process.stdin) {
      bytes += chunk.length;
      if (bytes > 256 * 1024) throw new Error('input_limit');
      chunks.push(chunk);
    }
    request = JSON.parse(Buffer.concat(chunks).toString('utf8'));
  }
  await runCommand(runtime, command, request, emit);
} catch (error) {
  const code = classifyError(error, command === 'infer' ? 'inference' : 'auth');
  emit({ type: 'error', code });
  process.exitCode = code === 'auth_required' ? 20 : code === 'rate_limited' ? 22 : 21;
}
