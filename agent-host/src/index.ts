import { mkdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { StringDecoder } from "node:string_decoder";
import { Type, type Static } from "typebox";
import { Value } from "typebox/value";
import { ModelRuntime, type AgentSession } from "@earendil-works/pi-coding-agent";
import { createTools, toolNames } from "./tools.js";
import { outputSchemas, parseOutput } from "./outputs.js";
import { createBusinessSession } from "./session.js";

// Dedicated application Pi directory; never point this at the Gmail credential directory.
const root = resolve(process.argv[2] ?? join(process.cwd(), ".tools-touch-pi"));
mkdirSync(root, { recursive: true });
const emit = (value: unknown) => process.stdout.write(JSON.stringify(value) + "\n");
const runtime = await ModelRuntime.create({ authPath: join(root, "auth.json"), modelsPath: null,
  modelsStorePath: join(root, "models-store.json"), refreshOnCreate: false });
const string = Type.String({ minLength: 1, maxLength: 100000 });
const commandSchema = Type.Union([
  Type.Object({ type: Type.Literal("status"), id: string }, { additionalProperties: false }),
  Type.Object({ type: Type.Literal("cancel"), id: string }, { additionalProperties: false }),
  Type.Object({ type: Type.Literal("login"), id: string }, { additionalProperties: false }),
  Type.Object({ type: Type.Literal("auth_reply"), id: string, prompt_id: string, value: string }, { additionalProperties: false }),
  Type.Object({ type: Type.Literal("run"), id: string, run_id: string, prompt: string, model: string,
    output_kind: Type.Union([Type.Literal("research"), Type.Literal("analysis"), Type.Literal("draft")]),
    max_tool_calls: Type.Integer({ minimum: 1, maximum: 100 }), timeout_ms: Type.Integer({ minimum: 1000, maximum: 900000 }) }, { additionalProperties: false }),
  Type.Object({ type: Type.Literal("tool_result"), id: string, result: Type.Unknown() }, { additionalProperties: false }),
]);
let session: AgentSession | undefined;
let busy = false;
let controller: AbortController | undefined;
const pending = new Map<string, { resolve: (result: unknown) => void; reject: (error: Error) => void }>();
const authPrompts = new Map<string, (value: string) => void>();

async function handle(input: unknown) {
  if (!Value.Check(commandSchema, input)) { emit({ type: "error", code: "INVALID_COMMAND" }); return; }
  const command = input as Static<typeof commandSchema>;
  if (command.type === "auth_reply") {
    const reply = authPrompts.get(command.prompt_id);
    if (reply) reply(command.value);
    emit({ type: "response", id: command.id, ok: Boolean(reply), ...(!reply ? { code: "AUTH_PROMPT_EXPIRED" } : {}) });
    return;
  }
  if (command.type === "tool_result") { pending.get(command.id)?.resolve(command.result); return; }
  if (command.type === "cancel") {
    controller?.abort();
    await session?.abort();
    emit({ type: "response", id: command.id, ok: true });
    return;
  }
  if (command.type === "status") {
    emit({ type: "response", id: command.id, ok: true, data: {
      provider: "openai-codex", configured: runtime.hasConfiguredAuth("openai-codex"),
      models: runtime.getModels("openai-codex").map(model => ({ id: model.id, name: model.name })), busy,
    } });
    return;
  }
  if (busy) { emit({ type: "response", id: command.id, ok: false, code: "RUN_BUSY" }); return; }
  if (command.type === "login") {
    busy = true;
    controller = new AbortController();
    const loginController = controller;
    const deadline = setTimeout(() => loginController.abort(), 300000);
    emit({ type: "response", id: command.id, ok: true });
    try {
      await runtime.login("openai-codex", "oauth", {
        signal: loginController.signal,
        notify: event => {
          if (event.type === "auth_url") emit({ type: "auth_url", url: event.url });
          else if (event.type === "progress" || event.type === "info") emit({ type: "auth_progress", message: event.message });
        },
        prompt: async prompt => {
          if (prompt.type === "secret") throw new Error("SECRET_PROMPT_UNSUPPORTED");
          const signal = prompt.signal ? AbortSignal.any([prompt.signal, loginController.signal]) : loginController.signal;
          const id = crypto.randomUUID();
          return await new Promise<string>((resolveValue, reject) => {
            const cleanup = () => { authPrompts.delete(id); signal.removeEventListener("abort", abort); };
            const abort = () => { cleanup(); emit({ type: "auth_prompt_closed", prompt_id: id }); reject(new Error("AUTH_CANCELLED")); };
            authPrompts.set(id, value => { cleanup(); resolveValue(value); });
            signal.addEventListener("abort", abort, { once: true });
            if (signal.aborted) { abort(); return; }
            emit({ type: "auth_prompt", prompt_id: id, kind: prompt.type, message: prompt.message,
              ...(prompt.type === "select" ? { options: prompt.options } : {}) });
          });
        },
      });
      emit({ type: "auth_finished", ok: true });
    } catch { emit({ type: "auth_finished", ok: false, code: loginController.signal.aborted ? "AUTH_CANCELLED" : "AUTH_FAILED" }); }
    finally { clearTimeout(deadline); authPrompts.clear(); controller = undefined; busy = false; }
    return;
  }
  const model = runtime.getModel("openai-codex", command.model);
  if (!model) { emit({ type: "response", id: command.id, ok: false, code: "MODEL_NOT_FOUND" }); return; }
  busy = true;
  controller = new AbortController();
  const runController = controller;
  const timer = setTimeout(() => runController.abort(new Error("RUN_TIMEOUT")), command.timeout_ms);
  let toolCalls = 0;
  let accepted = false;
  let sequence = 0;
  const event = (type: string, data: unknown) => emit({ type, run_id: command.run_id, sequence: ++sequence, data });
  try {
    const customTools = createTools(async (name, args, callId, toolSignal) => {
      if (name === "create_outreach_draft" && command.output_kind !== "draft") throw new Error("DRAFT_NOT_REQUESTED");
      runController.signal.throwIfAborted();
      if (++toolCalls > command.max_tool_calls) {
        runController.abort(new Error("TOOL_BUDGET_EXCEEDED"));
        throw new Error("TOOL_BUDGET_EXCEEDED");
      }
      const signal = toolSignal ? AbortSignal.any([toolSignal, runController.signal]) : runController.signal;
      const key = `${command.run_id}:${callId}`;
      return await new Promise<unknown>((resolveResult, reject) => {
        const cleanup = () => { pending.delete(key); signal.removeEventListener("abort", abort); };
        const abort = () => { cleanup(); reject(new Error("CANCELLED")); };
        pending.set(key, {
          resolve: result => { cleanup(); resolveResult(result); },
          reject: error => { cleanup(); reject(error); },
        });
        signal.addEventListener("abort", abort, { once: true });
        if (signal.aborted) { abort(); return; }
        emit({ type: "tool_request", id: key, run_id: command.run_id, tool: name, arguments: args });
      });
    });
    const created = await createBusinessSession(root, runtime, command.model, customTools);
    session = created.session;
    const active = session;
    const onAbort = () => { void active.abort().catch(() => {}); };
    runController.signal.addEventListener("abort", onAbort, { once: true });
    emit({ type: "response", id: command.id, ok: true });
    accepted = true;
    event("run_started", { session_id: active.sessionId });
    const unsubscribe = active.subscribe(update => {
      // Never forward raw reasoning, credentials, model requests or arbitrary SDK objects.
      if (update.type === "tool_execution_start") event("tool_started", { tool: update.toolName });
      if (update.type === "tool_execution_end") event("tool_finished", { tool: update.toolName, is_error: update.isError });
    });
    try {
      runController.signal.throwIfAborted();
      await active.prompt(command.prompt + "\nReturn the final response as ONLY a JSON object matching this JSON Schema. Use real IDs returned by tools. No markdown fences.\n" + JSON.stringify(outputSchemas[command.output_kind]));
      runController.signal.throwIfAborted();
      const last = [...active.messages].reverse().find(message => message.role === "assistant");
      if (last?.role === "assistant" && (last.stopReason === "error" || last.stopReason === "aborted")) throw new Error("MODEL_REQUEST_FAILED");
      const text = last?.role === "assistant" ? last.content.filter(block => block.type === "text").map(block => block.text).join("\n") : "";
      event("run_finished", { state: "Completed", output: parseOutput(command.output_kind, text) });
    } finally {
      unsubscribe();
      runController.signal.removeEventListener("abort", onAbort);
      active.dispose();
    }
  } catch (error) {
    if (!accepted) emit({ type: "response", id: command.id, ok: false, code: "SESSION_START_FAILED" });
    const reason = runController.signal.reason;
    const code = error instanceof Error && error.message === "INVALID_STAGE_OUTPUT" ? error.message :
      reason instanceof Error && ["RUN_TIMEOUT", "TOOL_BUDGET_EXCEEDED"].includes(reason.message) ? reason.message : "RUN_STOPPED";
    event("run_finished", { state: runController.signal.aborted ? "Cancelled" : "Failed", code });
  } finally {
    clearTimeout(timer);
    for (const callback of pending.values()) callback.reject(new Error("RUN_STOPPED"));
    pending.clear();
    session = undefined;
    controller = undefined;
    busy = false;
  }
}

const decoder = new StringDecoder("utf8");
let buffer = "";
process.stdin.on("data", (chunk: Buffer) => {
  buffer += decoder.write(chunk);
  if (Buffer.byteLength(buffer) > 2_000_000) { controller?.abort(); process.exitCode = 1; process.stdin.destroy(); return; }
  let delimiter: number;
  while ((delimiter = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, delimiter).replace(/\r$/, "");
    buffer = buffer.slice(delimiter + 1);
    try { void handle(JSON.parse(line)).catch(() => emit({ type: "error", code: "COMMAND_FAILED" })); }
    catch { emit({ type: "error", code: "INVALID_JSON" }); }
  }
});
process.stdin.on("end", () => { controller?.abort(); });
emit({ type: "ready", protocol_version: 1, tools: toolNames });
