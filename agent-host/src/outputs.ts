import { Type } from "typebox";
import { Value } from "typebox/value";

const text = Type.String({ minLength: 1, maxLength: 20000 });
const id = Type.String({ minLength: 1, maxLength: 128 });
const ids = Type.Array(id, { maxItems: 30 });
export const outputSchemas = {
  research: Type.Object({ summary: text, professor_ids: ids, paper_ids: ids }, { additionalProperties: false }),
  analysis: Type.Object({ professor_id: id, research_summary: text,
    personal_match: Type.Union([text, Type.Null()]), paper_ids: ids,
    evidence: Type.Array(Type.Object({ url: Type.String({ pattern: "^https?://", maxLength: 2048 }), claim: text }, { additionalProperties: false }), { minItems: 1, maxItems: 30 }),
  }, { additionalProperties: false }),
  draft: Type.Object({ draft_id: id }, { additionalProperties: false }),
};
export type OutputKind = keyof typeof outputSchemas;
export function parseOutput(kind: OutputKind, text: string): unknown {
  let result: unknown;
  try { result = JSON.parse(text); } catch { throw new Error("INVALID_STAGE_OUTPUT"); }
  if (!Value.Check(outputSchemas[kind], result)) throw new Error("INVALID_STAGE_OUTPUT");
  return result;
}
