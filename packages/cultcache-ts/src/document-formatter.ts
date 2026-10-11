import { decode, encode } from "@msgpack/msgpack";

import { MAX_PAYLOAD_DEPTH } from "./payload-depth";
import type { SlotsCodec } from "./codec";
import type { AnyCultCacheDocumentDefinition, CultCacheDocumentFormatter } from "./types";
import { readWire, writeWire } from "./wire";

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
  // The encoder's own depth refusal (100 by default) is set above the limit, so the payload walk decides.
  encode: (value: unknown) => encode(value, { maxDepth: MAX_PAYLOAD_DEPTH * 2 }),
  decode: (payload: Uint8Array) => decode(payload),
};

// The formatter a document gets from a slot codec: the codec's value as one MessagePack payload. Private to this module
// for the same reason as the resolver: exported, it would read a payload without the schema.
function codecFormatter<TValue extends object>(codec: SlotsCodec<TValue>): CultCacheDocumentFormatter<TValue> {
  return {
    encode: (value) => writeWire(codec.encode(value)),
    decode: (payload) => codec.decode(readWire(payload)),
  };
}
