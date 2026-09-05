import { Type } from "typebox";
import { Value } from "typebox/value";

const text = Type.String({ minLength: 1, maxLength: 20000 });
const id = Type.String({ minLength: 1, maxLength: 128 });
const ids = Type.Array(id, { maxItems: 30 });
const candidateKind = Type.Union([Type.Literal("DepartmentProgram"), Type.Literal("ProfessorAppointment")]);
const score = Type.Union([Type.Literal(0), Type.Literal(0.25), Type.Literal(0.5), Type.Literal(0.75), Type.Literal(1), Type.Null()]);
const citation = Type.Object({
  evidence_id: id,
  url: Type.String({ pattern: "^https?://", maxLength: 2048 }),
  claim: text,
}, { additionalProperties: false });
export const outputSchemas = {
  research: Type.Object({ summary: text, professor_ids: ids, paper_ids: ids }, { additionalProperties: false }),
  analysis: Type.Object({ professor_id: id, research_summary: text,
    personal_match: Type.Union([text, Type.Null()]), paper_ids: ids,
    evidence: Type.Array(Type.Object({ url: Type.String({ pattern: "^https?://", maxLength: 2048 }), claim: text }, { additionalProperties: false }), { minItems: 1, maxItems: 30 }),
  }, { additionalProperties: false }),
  semantic: Type.Object({
    candidate_scope_id: id,
    evaluations: Type.Array(Type.Object({
      target_kind: candidateKind,
      target_id: id,
      components: Type.Array(Type.Object({
        key: id,
        score,
        citations: Type.Array(citation, { minItems: 1, maxItems: 10 }),
        unknown_reason: Type.Optional(text),
      }, { additionalProperties: false }), { minItems: 1, maxItems: 12 }),
      reasons: Type.Optional(Type.Array(text, { maxItems: 10 })),
    }, { additionalProperties: false }), { maxItems: 200 }),
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
