import { join } from "node:path";
import { createAgentSession, SessionManager, SettingsManager, type ModelRuntime } from "@earendil-works/pi-coding-agent";
import { createResources } from "./resources.js";
import { createTools, toolNames } from "./tools.js";

export async function createBusinessSession(root: string, runtime: ModelRuntime, modelId: string, customTools: ReturnType<typeof createTools>, provider = "openai-codex") {
  const model = runtime.getModel(provider, modelId);
  if (!model) throw new Error("MODEL_NOT_FOUND");
  return await createAgentSession({
    cwd: root, agentDir: root, modelRuntime: runtime, model,
    tools: toolNames, noTools: "builtin", customTools, resourceLoader: createResources(),
    settingsManager: SettingsManager.inMemory({ retry: { enabled: false } }),
    sessionManager: SessionManager.create(root, join(root, "sessions")),
  });
}
