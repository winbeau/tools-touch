import { join } from "node:path";
import { ModelRuntime } from "@earendil-works/pi-coding-agent";

export function createAccountRuntime(root: string) {
  // Refresh local credential availability at startup without fetching a remote catalogue.
  // refreshOnCreate:false leaves stored accounts looking disconnected after restart.
  return ModelRuntime.create({ authPath: join(root, "auth.json"), modelsPath: null,
    modelsStorePath: join(root, "models-store.json"), allowModelNetwork: false });
}
