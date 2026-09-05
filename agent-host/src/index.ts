import { mkdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { StringDecoder } from "node:string_decoder";
import { Type, type Static } from "typebox";
import { Value } from "typebox/value";
import { type AgentSession } from "@earendil-works/pi-coding-agent";
import { createTools, policyTools } from "./tools.js";
import { outputSchemas, parseOutput } from "./outputs.js";
import { createBusinessSession } from "./session.js";
import { Authentication } from "./authentication.js";
import { createAccountRuntime } from "./runtime.js";

const PROTOCOL_VERSION = 2;
const MAX_FRAME_BYTES = 1_048_576;

// Dedicated application Pi directory; never point this at the Gmail credential directory.
const root = resolve(process.argv[2] ?? join(process.cwd(), ".tools-touch-pi"));
mkdirSync(root, { recursive: true });
const emit = (value: object) => process.stdout.write(JSON.stringify({ protocol_version: PROTOCOL_VERSION, ...value }) + "\n");
const emitError = (code: string, message = code, retryable = false) => emit({ type: "error", code, message, retryable });
const runtime = await createAccountRuntime(root);
const validatedProviders = new Map<string, string>();
type ProviderDefinition = {
  id: string;
  name: string;
  auth: { oauth?: unknown; apiKey?: { login?: unknown } };
};
const providerView = (provider: ProviderDefinition) => {
  const authMethods = [
    ...(provider.auth.oauth ? [{ id: "oauth", requires_secret: false, interactive: true }] : []),
    ...(provider.auth.apiKey?.login ? [{ id: "api_key", requires_secret: true, interactive: false }] : []),
  ];
  const lastValidatedAt = validatedProviders.get(provider.id) ?? null;
  return {
    id: provider.id, name: provider.name, configured: runtime.hasConfiguredAuth(provider.id),
    verified: lastValidatedAt !== null, state: lastValidatedAt !== null ? "Verified" : runtime.hasConfiguredAuth(provider.id) ? "Configured" : "Disconnected",
    last_validated_at: lastValidatedAt, auth_methods: authMethods,
    oauth: authMethods.some(method => method.id === "oauth"), api_key: authMethods.some(method => method.id === "api_key"),
    models: runtime.getModels(provider.id).map(model => ({ id: model.id, name: model.name })),
  };
};
const string = Type.String({ minLength: 1, maxLength: 100000 });
const commandSchema = Type.Union([
  Type.Object({ protocol_version: Type.Literal(PROTOCOL_VERSION), type: Type.Literal("status"), id: string, provider: Type.Optional(string) }, { additionalProperties: false }),
  Type.Object({ protocol_version: Type.Literal(PROTOCOL_VERSION), type: Type.Literal("login"), id: string, provider: string, auth_type: Type.Union([Type.Literal("oauth"), Type.Literal("api_key")]), method: Type.Optional(string), secret: Type.Optional(string) }, { additionalProperties: false }),
  Type.Object({ protocol_version: Type.Literal(PROTOCOL_VERSION), type: Type.Literal("auth_reply"), id: string, auth_request_id: string, prompt_id: string, value: string }, { additionalProperties: false }),
  Type.Object({ protocol_version: Type.Literal(PROTOCOL_VERSION), type: Type.Literal("cancel_auth"), id: string, auth_request_id: string }, { additionalProperties: false }),
  Type.Object({ protocol_version: Type.Literal(PROTOCOL_VERSION), type: Type.Literal("cancel_run"), id: string, run_id: string, stage_key: string, attempt_id: string }, { additionalProperties: false }),
  Type.Object({ protocol_version: Type.Literal(PROTOCOL_VERSION), type: Type.Literal("run"), id: string, run_id: string, stage_key: string, attempt_id: string, provider: string,
    model: string, policy_id: Type.Union([Type.Literal("research"), Type.Literal("analysis"), Type.Literal("semantic"), Type.Literal("draft")]),
    input_context: Type.Object({ prompt: string, output_kind: Type.Union([Type.Literal("research"), Type.Literal("analysis"), Type.Literal("semantic"), Type.Literal("draft")]) }, { additionalProperties: false }),
    limits: Type.Object({ max_tool_calls: Type.Integer({ minimum: 1, maximum: 100 }), timeout_ms: Type.Integer({ minimum: 1000, maximum: 900000 }) }, { additionalProperties: false }) }, { additionalProperties: false }),
  Type.Object({ protocol_version: Type.Literal(PROTOCOL_VERSION), type: Type.Literal("tool_result"), id: string, run_id: string, stage_key: string, attempt_id: string, tool_call_id: string, result: Type.Unknown() }, { additionalProperties: false }),
]);
let session: AgentSession | undefined;
let busy = false;
let controller: AbortController | undefined;
let activeRun: { run_id: string; stage_key: string; attempt_id: string } | undefined;
const pending = new Map<string, { resolve: (result: unknown) => void; reject: (error: Error) => void }>();
const authentication = new Authentication(runtime, emit);

async function handle(input: unknown) {
  if (!Value.Check(commandSchema, input)) { emitError("INVALID_COMMAND", "Command does not match the protocol schema."); return; }
  const command = input as Static<typeof commandSchema>;
  if (command.type === "auth_reply") {
    const reply = authentication.reply(command.auth_request_id, command.prompt_id, command.value);
    emit({ type: "response", id: command.id, ok: Boolean(reply), ...(!reply ? { code: "AUTH_PROMPT_EXPIRED" } : {}) });
    return;
  }
  if (command.type === "tool_result") {
    if (activeRun && command.run_id === activeRun.run_id && command.stage_key === activeRun.stage_key && command.attempt_id === activeRun.attempt_id)
      pending.get(command.tool_call_id)?.resolve(command.result);
    return;
  }
  if (command.type === "cancel_auth") {
    const cancelled = await authentication.cancel(command.auth_request_id);
    emit({ type: "response", id: command.id, ok: cancelled, ...(!cancelled ? { code: "AUTH_NOT_ACTIVE" } : {}) });
    return;
  }
  if (command.type === "cancel_run") {
    const matches = activeRun && command.run_id === activeRun.run_id && command.stage_key === activeRun.stage_key && command.attempt_id === activeRun.attempt_id;
    if (matches) { controller?.abort(); await session?.abort(); }
    emit({ type: "response", id: command.id, ok: Boolean(matches), ...(!matches ? { code: "RUN_TARGET_NOT_ACTIVE" } : {}) });
    return;
  }
  if (command.type === "status") {
    const provider = command.provider ?? "openai-codex";
    emit({ type: "response", id: command.id, ok: true, data: {
      provider, configured: runtime.hasConfiguredAuth(provider),
      models: runtime.getModels(provider).map(model => ({ id: model.id, name: model.name })), busy: busy || authentication.busy,
      active_run: activeRun ?? null, host_version: "0.1.0", pi_version: "0.85.0", output_schema_version: "2",
      providers: runtime.getProviders().filter(item => item.auth.oauth || item.auth.apiKey?.login).map(providerView),
    } });
    return;
  }
  if (busy) { emit({ type: "response", id: command.id, ok: false, code: "RUN_BUSY" }); return; }
  if (command.type === "login") { authentication.start(command); return; }
  if (authentication.busy) { emit({ type: "response", id: command.id, ok: false, code: "AUTH_IN_PROGRESS" }); return; }
  const provider = command.provider;
  const model = runtime.getModel(provider, command.model);
  if (!model) { emit({ type: "response", id: command.id, ok: false, code: "MODEL_NOT_FOUND" }); return; }
  busy = true;
  activeRun = { run_id: command.run_id, stage_key: command.stage_key, attempt_id: command.attempt_id };
  controller = new AbortController();
  const runController = controller;
  const outputKind = command.policy_id;
  if (command.input_context.output_kind !== outputKind) {
    busy = false; activeRun = undefined;
    emit({ type: "response", id: command.id, ok: false, code: "POLICY_OUTPUT_MISMATCH" });
    return;
  }
  const timer = setTimeout(() => runController.abort(new Error("RUN_TIMEOUT")), command.limits.timeout_ms);
  let toolCalls = 0;
  let accepted = false;
  let sequence = 0;
  const event = (type: string, data: unknown) => emit({ type, run_id: command.run_id, stage_key: command.stage_key, attempt_id: command.attempt_id, sequence: ++sequence, data });
  try {
    const allowedTools = policyTools[outputKind];
    const customTools = createTools(async (name, args, callId, toolSignal) => {
      if (name === "create_outreach_draft" && outputKind !== "draft") throw new Error("DRAFT_NOT_REQUESTED");
      runController.signal.throwIfAborted();
      if (++toolCalls > command.limits.max_tool_calls) {
        runController.abort(new Error("TOOL_BUDGET_EXCEEDED"));
        throw new Error("TOOL_BUDGET_EXCEEDED");
      }
      const signal = toolSignal ? AbortSignal.any([toolSignal, runController.signal]) : runController.signal;
      const key = `${command.run_id}:${command.stage_key}:${command.attempt_id}:${callId}`;
      return await new Promise<unknown>((resolveResult, reject) => {
        const cleanup = () => { pending.delete(key); signal.removeEventListener("abort", abort); };
        const abort = () => { cleanup(); reject(new Error("CANCELLED")); };
        pending.set(key, {
          resolve: result => { cleanup(); resolveResult(result); },
          reject: error => { cleanup(); reject(error); },
        });
        signal.addEventListener("abort", abort, { once: true });
        if (signal.aborted) { abort(); return; }
        emit({ type: "tool_request", run_id: command.run_id, stage_key: command.stage_key, attempt_id: command.attempt_id, sequence: ++sequence, tool_call_id: key, tool: name, arguments: args });
      });
    });
    const created = await createBusinessSession(root, runtime, command.model, customTools, provider, allowedTools);
    session = created.session;
    const active = session;
    const onAbort = () => { void active.abort().catch(() => {}); };
    runController.signal.addEventListener("abort", onAbort, { once: true });
    emit({ type: "response", id: command.id, ok: true });
    accepted = true;
    event("run_started", { session_id: active.sessionId });
    const unsubscribe = active.subscribe(update => {
      // Never forward raw reasoning, credentials, model requests or arbitrary SDK objects.
      if (update.type === "tool_execution_start") event("progress", { stage: "tool_started", tool: update.toolName });
      if (update.type === "tool_execution_end") event("progress", { stage: "tool_finished", tool: update.toolName, is_error: update.isError });
    });
    try {
      runController.signal.throwIfAborted();
      await active.prompt(command.input_context.prompt + "\nReturn the final response as ONLY a JSON object matching this JSON Schema. Use real IDs returned by tools. No markdown fences.\n" + JSON.stringify(outputSchemas[outputKind]));
      runController.signal.throwIfAborted();
      const last = [...active.messages].reverse().find(message => message.role === "assistant");
      if (last?.role === "assistant" && (last.stopReason === "error" || last.stopReason === "aborted")) throw new Error("MODEL_REQUEST_FAILED");
      const text = last?.role === "assistant" ? last.content.filter(block => block.type === "text").map(block => block.text).join("\n") : "";
      validatedProviders.set(provider, new Date().toISOString());
      event("run_finished", { state: "Completed", output: parseOutput(outputKind, text) });
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
    activeRun = undefined;
  }
}

const decoder = new StringDecoder("utf8");
let buffer = "";
process.stdin.on("data", (chunk: Buffer) => {
  buffer += decoder.write(chunk);
  if (Buffer.byteLength(buffer) > MAX_FRAME_BYTES + 1) { controller?.abort(); emitError("AGENT_FRAME_TOO_LARGE", "JSONL frame exceeds the byte limit."); process.exitCode = 1; process.stdin.destroy(); return; }
  let delimiter: number;
  while ((delimiter = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, delimiter).replace(/\r$/, "");
    buffer = buffer.slice(delimiter + 1);
    if (Buffer.byteLength(line) > MAX_FRAME_BYTES) { emitError("AGENT_FRAME_TOO_LARGE", "JSONL frame exceeds the byte limit."); process.exitCode = 1; return; }
    try { void handle(JSON.parse(line)).catch(() => emitError("COMMAND_FAILED", "Command handling failed.", true)); }
    catch { emitError("INVALID_JSON", "Input is not valid JSON."); }
  }
});
process.stdin.on("end", () => { controller?.abort(); void authentication.cancel(); });
emit({ type: "ready", host_version: "0.1.0", pi_version: "0.85.0", supported_policies: ["research", "analysis", "semantic", "draft"], provider_catalog: runtime.getProviders()
  .filter(provider => provider.auth.oauth || provider.auth.apiKey?.login).map(providerView) });
