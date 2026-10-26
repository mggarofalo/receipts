// Provider capability only: no agent session, tools, extensions, or filesystem context.
export const provider = 'openai-codex';
export const deviceUrl = 'https://auth.openai.com/codex/device';

export async function runCommand(runtime, command, request, emit) {
  switch (command) {
    case 'login':
      await runtime.login(provider, 'oauth', {
        prompt: async prompt => {
          if (prompt.type === 'select' && prompt.options?.some(x => x.id === 'device_code')) return 'device_code';
          throw new Error('unsupported_auth_interaction');
        },
        notify: event => {
          if (event.type !== 'device_code') return;
          if (event.verificationUri !== deviceUrl || !/^[A-Za-z0-9-]{1,32}$/.test(event.userCode))
            throw new Error('invalid_device_code');
          if (!Number.isSafeInteger(event.expiresInSeconds) || event.expiresInSeconds < 1 || event.expiresInSeconds > 1200)
            throw new Error('invalid_device_code');
          emit({ type: 'device_code', url: deviceUrl, code: event.userCode, expiresInSeconds: event.expiresInSeconds });
        },
      });
      emit({ type: 'ready' });
      return;
    case 'logout':
      await runtime.logout(provider);
      emit({ type: 'logged_out' });
      return;
    case 'status':
      emit({ type: (await runtime.checkAuth(provider))?.type === 'oauth' ? 'ready' : 'auth_required' });
      return;
    case 'models':
      emit({ type: 'models', models: runtime.getModels(provider)
        .filter(model => model.input?.includes('text')).map(model => model.id) });
      return;
    case 'infer':
      break;
    default:
      throw new Error('unknown_command');
  }
  if ((await runtime.checkAuth(provider))?.type !== 'oauth') throw new Error('auth_required');
  if (!request || typeof request.model !== 'string' || request.model.length > 128 ||
      typeof request.instructions !== 'string' || request.instructions.length > 32768 ||
      typeof request.prompt !== 'string' || request.prompt.length > 131072 ||
      !request.schema || typeof request.schema !== 'object' || Array.isArray(request.schema) ||
      !['low', 'medium', 'high'].includes(request.reasoning)) throw new Error('input_limit');
  const model = runtime.getModel(provider, request.model);
  if (!model?.input?.includes('text') || !Number.isFinite(model.contextWindow) || model.contextWindow <= 0)
    throw new Error('model_unavailable');
  const systemPrompt = request.instructions + '\nReturn one JSON object conforming to this schema. No Markdown.\n' + JSON.stringify(request.schema);
  // A conservative admission bound, not a claim of provider-enforced token limits.
  if (Buffer.byteLength(systemPrompt + request.prompt, 'utf8') + 16000 > model.contextWindow)
    throw new Error('context_limit');
  const result = await runtime.completeSimple(model, {
    systemPrompt,
    messages: [{ role: 'user', content: [{ type: 'text', text: request.prompt }], timestamp: Date.now() }],
    tools: [],
  }, { reasoning: request.reasoning, maxTokens: 16000 });
  if (result.stopReason === 'error' || result.stopReason === 'aborted') throw new Error(result.errorMessage || 'inference_failed');
  if (result.model !== model.id || result.provider !== provider || result.stopReason !== 'stop' ||
      !Array.isArray(result.content) || result.content.some(part =>
        (part.type !== 'text' && part.type !== 'thinking') || (part.type === 'text' && typeof part.text !== 'string')))
    throw new Error('incomplete_response');
  const text = result.content.filter(part => part.type === 'text').map(part => part.text).join('');
  if (!text || Buffer.byteLength(text, 'utf8') > 512 * 1024) throw new Error('output_limit');
  const usageKeys = ['input', 'output', 'cacheRead', 'cacheWrite'];
  const usage = result.usage && usageKeys.every(key => Number.isSafeInteger(result.usage[key]) && result.usage[key] >= 0)
    ? Object.fromEntries(usageKeys.map(key => [key, result.usage[key]])) : undefined;
  emit({ type: 'result', text, ...(usage ? { usage } : {}) });
}
