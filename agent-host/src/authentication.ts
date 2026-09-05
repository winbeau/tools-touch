import type { ModelRuntime } from "@earendil-works/pi-coding-agent";

export type LoginCommand = { id: string; provider?: string; auth_type?: "oauth" | "api_key"; method?: string; secret?: string };
export function authErrorCode(error: unknown): string {
  const chain: string[] = [];
  let current: unknown = error;
  for (let n = 0; current instanceof Error && n < 6; n++) {
    chain.push(current.message, String((current as NodeJS.ErrnoException).code ?? ""));
    current = current.cause;
  }
  // Classify nested SDK causes without emitting raw messages, tokens, or URLs.
  const message = chain.join(" ");
  if (/EADDRINUSE|address already in use/i.test(message)) return "AUTH_CALLBACK_PORT_BUSY";
  if (/state.*mismatch|state.*invalid/i.test(message)) return "AUTH_STATE_MISMATCH";
  if (/fetch failed|ECONN|ENOTFOUND|ETIMEDOUT|UND_ERR_CONNECT_TIMEOUT|network/i.test(message)) return "AUTH_NETWORK_FAILED";
  if (/EACCES|EPERM|EROFS|ENOSPC/i.test(message)) return "AUTH_CREDENTIAL_SAVE_FAILED";
  if (/401|403|access.denied|invalid.grant/i.test(message)) return "AUTH_REJECTED";
  return "AUTH_FAILED";
}

// The host owns one login at a time. Completion is emitted only after the busy
// state has cleared, so an immediate status request or retry sees consistent state.
export class Authentication {
  private active?: { controller: AbortController; completion: Promise<void> };
  private readonly prompts = new Map<string, (value: string) => boolean>();
  constructor(private runtime: Pick<ModelRuntime, "login" | "getProvider">, private emit: (event: object) => void) {}
  get busy() { return this.active !== undefined; }
  reply(id: string, value: string) { const reply = this.prompts.get(id); return reply?.(value) ?? false; }
  async cancel() { const active = this.active; active?.controller.abort(); await active?.completion; }
  start(command: LoginCommand): void {
    if (this.busy) { this.emit({ type: "response", id: command.id, ok: false, code: "AUTH_IN_PROGRESS" }); return; }
    const provider = command.provider ?? "openai-codex";
    const authType = command.auth_type ?? "oauth";
    const definition = this.runtime.getProvider(provider);
    if (!definition || (authType === "oauth" ? !definition.auth.oauth : !definition.auth.apiKey?.login)) {
      this.emit({ type: "response", id: command.id, ok: false, code: "AUTH_METHOD_UNSUPPORTED" }); return;
    }
    const controller = new AbortController();
    const active = { controller, completion: Promise.resolve() };
    this.active = active;
    this.emit({ type: "response", id: command.id, ok: true });
    // Defer work until active.completion is installed, including synchronously failing SDK calls.
    active.completion = Promise.resolve().then(async () => {
      const deadline = setTimeout(() => controller.abort(), command.method === "device_code" ? 900000 : 300000);
      let result: object = { type: "auth_finished", provider, ok: true };
      let secret = command.secret;
      delete command.secret;
      try {
        await this.runtime.login(provider, authType, {
          signal: controller.signal,
          notify: event => {
            if (event.type === "auth_url") this.emit({ type: "auth_url", provider, url: event.url });
            else if (event.type === "device_code") this.emit({ type: "auth_device_code", provider, code: event.userCode, url: event.verificationUri });
            // SDK prose may contain credentials or internal names. UI uses its own stage labels.
            else this.emit({ type: "auth_progress", provider, stage: "authorizing" });
          },
          prompt: async prompt => {
            controller.signal.throwIfAborted();
            if (prompt.type === "secret" && secret) { const value = secret; secret = undefined; return value; }
            if (prompt.type === "select" && command.method && prompt.options.some(option => option.id === command.method)) return command.method;
            const signal = prompt.signal ? AbortSignal.any([prompt.signal, controller.signal]) : controller.signal;
            const id = crypto.randomUUID();
            return await new Promise<string>((resolve, reject) => {
              const cleanup = () => { this.prompts.delete(id); signal.removeEventListener("abort", abort); this.emit({ type: "auth_prompt_closed", prompt_id: id }); };
              const abort = () => {
                cleanup();
                if (prompt.type === "manual_code" && !controller.signal.aborted)
                  this.emit({ type: "auth_progress", provider, stage: "verifying_credentials" });
                reject(new Error("AUTH_CANCELLED"));
              };
              this.prompts.set(id, value => {
                if (prompt.type === "select" && !prompt.options.some(option => option.id === value)) return false;
                cleanup(); resolve(value); return true;
              });
              signal.addEventListener("abort", abort, { once: true });
              if (signal.aborted) { abort(); return; }
              this.emit({ type: "auth_prompt", provider, prompt_id: id, kind: prompt.type,
                ...(prompt.type === "select" ? { options: prompt.options.map(option => ({ id: option.id, label: option.label })) } : {}) });
            });
          },
        });
      } catch (error) {
        result = { type: "auth_finished", provider, ok: false, code: controller.signal.aborted ? "AUTH_CANCELLED" : authErrorCode(error) };
      } finally {
        secret = undefined; clearTimeout(deadline); this.prompts.clear(); this.active = undefined;
        this.emit(result);
      }
    });
  }
}
