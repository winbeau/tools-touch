import { test } from "node:test";
import assert from "node:assert/strict";
import { ModelRuntime } from "@earendil-works/pi-coding-agent";
import { createAccountRuntime } from "./runtime.js";
import { Authentication, authErrorCode } from "./authentication.js";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createBusinessSession } from "./session.js";
import { createTools, toolNames } from "./tools.js";

const tick = () => new Promise<void>(resolve => setImmediate(resolve));
function fixture(login: (...args: Parameters<ModelRuntime["login"]>) => Promise<unknown>) {
  const events: any[] = [];
  const runtime = { login, getProvider: () => ({ auth: { oauth: {}, apiKey: { login() {} } } }) } as unknown as ModelRuntime;
  const auth = new Authentication(runtime, event => {
    if ((event as any).type === "auth_finished") assert.equal(auth.busy, false, "completion must clear busy before notification");
    events.push(event);
  });
  return { auth, events };
}

test("OAuth chooses browser or device method without an orphaned initial prompt", async () => {
  for (const method of ["browser", "device_code"]) {
    const { auth, events } = fixture(async (provider, type, interaction) => {
      assert.equal(provider, "openai-codex"); assert.equal(type, "oauth");
      assert.equal(await interaction.prompt({ type: "select", message: "Select", options: [{ id: "browser", label: "Browser" }, { id: "device_code", label: "Device" }] }), method);
      if (method === "browser") interaction.notify({ type: "auth_url", url: "https://example.org/authorize" });
      else interaction.notify({ type: "device_code", userCode: "ABCD-1234", verificationUri: "https://example.org/authorize" });
    });
    auth.start({ id: method, method });
    await tick();
    assert.equal(events.some(event => event.type === "auth_prompt"), false);
    assert.equal(events.at(-1).ok, true);
    const authorization = events.find(event => event.type === (method === "browser" ? "auth_url" : "auth_device_code"));
    assert.equal(authorization.url, "https://example.org/authorize");
    if (method === "device_code") assert.equal(authorization.code, "ABCD-1234");
  }
});

test("pending login rejects duplicates, cancels, and permits immediate retry", async () => {
  const { auth, events } = fixture(async (_provider, _type, interaction) => {
    await interaction.prompt({ type: "manual_code", message: "Paste code" });
  });
  auth.start({ id: "first" }); await tick();
  const prompt = events.find(event => event.type === "auth_prompt");
  auth.start({ id: "duplicate" });
  assert.equal(events.at(-1).code, "AUTH_IN_PROGRESS");
  await auth.cancel();
  assert.equal(auth.busy, false);
  assert.equal(events.at(-1).code, "AUTH_CANCELLED");
  assert.equal(auth.reply(prompt.prompt_id, "expired"), false);
  auth.start({ id: "retry" }); await tick();
  assert.equal(auth.reply(events.filter(event => event.type === "auth_prompt").at(-1).prompt_id, "synthetic-code"), true);
  await tick();
  assert.equal(events.at(-1).ok, true);
});

test("API key is consumed once and never echoed through diagnostic events", async () => {
  const secret = "synthetic-test-key-not-a-real-credential";
  const { auth, events } = fixture(async (_provider, _type, interaction) => {
    assert.equal(await interaction.prompt({ type: "secret", message: "Key" }), secret);
    interaction.notify({ type: "progress", message: secret });
    throw new Error("fetch failed " + secret);
  });
  const command = { id: "key", provider: "deepseek", auth_type: "api_key" as const, secret };
  auth.start(command); await tick();
  assert.equal(command.secret, undefined);
  assert.equal(JSON.stringify(events).includes(secret), false);
  assert.equal(events.at(-1).code, "AUTH_NETWORK_FAILED");
  assert.equal(authErrorCode(new Error("listen EADDRINUSE")), "AUTH_CALLBACK_PORT_BUSY");
});

test("real SDK saves independent provider credentials and selects the requested provider session", async () => {
  const directory = await mkdtemp(join(tmpdir(), "tools-touch-auth-"));
  try {
    const runtime = await createAccountRuntime(directory);
    for (const provider of ["openai", "deepseek", "anthropic", "zai", "zai-coding-cn"]) {
      assert.ok(runtime.getProvider(provider)?.auth.apiKey?.login, provider + " API key support missing");
      const events: any[] = [];
      const auth = new Authentication(runtime, event => events.push(event));
      auth.start({ id: provider, provider, auth_type: "api_key", secret: "synthetic-test-key" });
      for (let n = 0; auth.busy && n < 1000; n++) await new Promise(resolve => setTimeout(resolve, 10));
      assert.equal(auth.busy, false, provider + " did not finish");
      assert.equal(events.at(-1).type, "auth_finished");
      assert.equal(events.at(-1).ok, true, provider + ": " + events.at(-1).code);
      assert.equal(runtime.hasConfiguredAuth(provider), true);
      const model = runtime.getModels(provider)[0]!;
      assert.ok(model, provider + " models missing");
      const { session } = await createBusinessSession(directory, runtime, model.id, createTools(async () => ({})), provider);
      try {
        assert.equal(session.agent.state.model?.provider, provider);
        assert.deepEqual(session.getActiveToolNames().sort(), [...toolNames].sort());
      } finally { session.dispose(); }
    }
  } finally { await rm(directory, { recursive: true, force: true }); }
});


test("nested SDK failures retain a useful code without exposing response data", () => {
  const secret = "secret-token-never-emit";
  const nested = new Error("Provider login failed", { cause: new Error("fetch failed " + secret, { cause: Object.assign(new Error("connect"), { code: "ETIMEDOUT" }) }) });
  assert.equal(authErrorCode(nested), "AUTH_NETWORK_FAILED");
  assert.equal(authErrorCode(new Error("Provider login failed", { cause: new Error("EPERM " + secret) })), "AUTH_CREDENTIAL_SAVE_FAILED");
});

test("browser callback closes manual prompt while credential exchange is still pending", async () => {
  let finish!: () => void;
  const { auth, events } = fixture(async (_provider, _type, interaction) => {
    const callback = new AbortController();
    const input = interaction.prompt({ type: "manual_code", message: "code", signal: callback.signal }).catch(() => {});
    callback.abort(); await input;
    await new Promise<void>(resolve => { finish = resolve; });
  });
  auth.start({ id: "callback" }); await tick();
  assert.equal(auth.busy, true);
  assert.ok(events.some(e => e.type === "auth_progress" && e.stage === "verifying_credentials"));
  assert.equal(events.some(e => e.type === "auth_finished"), false);
  finish(); await tick();
  assert.equal(events.at(-1).ok, true);
});


test("real OpenAI SDK completes code exchange and persists credentials before success", async () => {
  const directory = await mkdtemp(join(tmpdir(), "tools-touch-openai-"));
  const originalFetch = globalThis.fetch;
  const runtime = await createAccountRuntime(directory);
  const access = Buffer.from("{}").toString("base64") + "." + Buffer.from(JSON.stringify({ "https://api.openai.com/auth": { chatgpt_account_id: "synthetic-account" } })).toString("base64") + ".synthetic";
  let exchanges = 0;
  let auth!: Authentication;
  const events: any[] = [];
  globalThis.fetch = async (input, init) => {
    const url = String(input);
    assert.equal(url, "https://auth.openai.com/oauth/token", "unexpected external request during isolated OAuth fixture");
    const body = new URLSearchParams(init?.body as URLSearchParams);
    assert.equal(body.get("code"), "synthetic-code");
    assert.ok(body.get("code_verifier"));
    exchanges++;
    return new Response(JSON.stringify({ access_token: access, refresh_token: "synthetic-refresh", expires_in: 3600 }), { headers: { "Content-Type": "application/json" } });
  };
  auth = new Authentication(runtime, event => {
    events.push(event);
    const value = event as any;
    // The real SDK supports manual callback entry; no real browser/account or port request is needed.
    if (value.type === "auth_prompt" && value.kind === "manual_code")
      queueMicrotask(() => { auth.reply(value.prompt_id, "synthetic-code"); });
  });
  try {
    auth.start({ id: "real-sdk", provider: "openai-codex", method: "browser" });
    for (let n = 0; auth.busy && n < 1000; n++) await new Promise(resolve => setTimeout(resolve, 10));
    assert.equal(auth.busy, false);
    assert.equal(exchanges, 1);
    assert.equal(events.at(-1).ok, true, events.at(-1).code);
    assert.equal(runtime.hasConfiguredAuth("openai-codex"), true);
    const restored = await createAccountRuntime(directory);
    assert.equal(restored.hasConfiguredAuth("openai-codex"), true);
    assert.equal(JSON.stringify(events).includes("synthetic-refresh"), false);
  } finally {
    await auth.cancel(); globalThis.fetch = originalFetch;
    await rm(directory, { recursive: true, force: true });
  }
});
