import { decode, encode } from "@msgpack/msgpack";

import { codecFormatter } from "./codec";
import type { AnyCultCacheDocumentDefinition, CultCacheDocumentFormatter } from "./types";

// The one place that decides how a document is encoded: its own formatter, or its codec's, or a plain map. A codec and a
// formatter are two answers to one question, so a definition that carries both is refused wherever it enters (the
// builder resolves through here as well). Package-internal: no entry of the package's exports map names this module, so
// a consumer cannot import the formatter and read a payload around the schema.
export function resolveDocumentFormatter(definition: AnyCultCacheDocumentDefinition): CultCacheDocumentFormatter<unknown> {
  if (definition.codec && definition.formatter) {
    throw new Error(`CultCache document type "${definition.type}" declares both a codec and a formatter.`);
  }
  return definition.formatter ?? (definition.codec ? codecFormatter(definition.codec) : plainDocumentFormatter);
}

const plainDocumentFormatter: CultCacheDocumentFormatter<unknown> = {
  encode: (value: unknown) => encode(value),
  decode: (payload: Uint8Array) => decode(payload),
};
