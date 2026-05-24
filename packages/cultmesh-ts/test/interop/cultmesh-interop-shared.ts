import { decode, encode } from "@msgpack/msgpack";

import { z } from "zod";
import { defineDocumentType } from "cultcache-ts";

export const INTEROP_DOCUMENT_TYPE = "cultmesh.interop-note";
export const INTEROP_SCHEMA_VERSION = "cultmesh.interop_note.v0";
export const INTEROP_BODY = "CultMesh local node state uses the shared CultCache wire store.";

export const interopNoteSchema = z.object({
  schemaVersion: z.literal(INTEROP_SCHEMA_VERSION),
  documentId: z.string().min(1),
  authorRuntimeId: z.string().min(1),
  verseId: z.string().min(1),
  body: z.string().min(1),
  tags: z.array(z.string().min(1)),
});

export type InteropNote = z.infer<typeof interopNoteSchema>;

export const interopNoteDocument = defineDocumentType({
  type: INTEROP_DOCUMENT_TYPE,
  schemaId: INTEROP_DOCUMENT_TYPE,
  schemaName: INTEROP_DOCUMENT_TYPE,
  schemaVersion: INTEROP_SCHEMA_VERSION,
  contentHash: INTEROP_DOCUMENT_TYPE,
  canonicalSchemaJson: "{\"schemaName\":\"cultmesh.interop-note\",\"schemaVersion\":\"cultmesh.interop_note.v0\",\"members\":[{\"slot\":0,\"name\":\"SchemaVersion\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false},{\"slot\":1,\"name\":\"DocumentId\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":true},{\"slot\":2,\"name\":\"AuthorRuntimeId\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false},{\"slot\":3,\"name\":\"VerseId\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false},{\"slot\":4,\"name\":\"Body\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false},{\"slot\":5,\"name\":\"Tags\",\"type\":\"System.String[]\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false}]}",
  compatibleSchemaIds: [INTEROP_DOCUMENT_TYPE],
  members: [
    { slot: 0, memberName: "SchemaVersion", typeName: "System.String" },
    { slot: 1, memberName: "DocumentId", typeName: "System.String", isName: true },
    { slot: 2, memberName: "AuthorRuntimeId", typeName: "System.String" },
    { slot: 3, memberName: "VerseId", typeName: "System.String" },
    { slot: 4, memberName: "Body", typeName: "System.String" },
    { slot: 5, memberName: "Tags", typeName: "System.String[]" },
  ],
  schema: interopNoteSchema,
  formatter: {
    encode(value: InteropNote): Uint8Array {
      return encode([
        value.schemaVersion,
        value.documentId,
        value.authorRuntimeId,
        value.verseId,
        value.body,
        value.tags,
      ]);
    },
    decode(payload: Uint8Array): InteropNote {
      const decoded = decode(payload);
      if (!Array.isArray(decoded) || decoded.length < 5) {
        throw new Error("CultMesh interop note payload must be a MessagePack slot array.");
      }

      const [schemaVersion, documentId, authorRuntimeId, verseId, body, tags] = decoded;
      return interopNoteSchema.parse({
        schemaVersion,
        documentId,
        authorRuntimeId,
        verseId,
        body,
        tags: Array.isArray(tags) ? tags : [],
      });
    },
  },
});

export function buildInteropNote(runtimeId: string, runtimeKind: string): InteropNote {
  return {
    schemaVersion: INTEROP_SCHEMA_VERSION,
    documentId: `note:${runtimeId}`,
    authorRuntimeId: runtimeId,
    verseId: "verse:interop",
    body: INTEROP_BODY,
    tags: [runtimeId, runtimeKind, "interop", "cultmesh"],
  };
}
