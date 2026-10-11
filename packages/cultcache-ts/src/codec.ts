import { decode, encode } from "@msgpack/msgpack";

import type { CultCacheDocumentFormatter, CultCacheSchemaCatalogMember } from "./types";

// A codec turns a value into the MessagePack-ready shape the reference runtimes write and back. Slot records are
// positional arrays with nil gaps; a union value is { kind: armName, ...armFields }, written [armKey, [armSlots...]].
// Refusals name the union or the slot and the key or code, and never echo a decoded value.
export interface Codec<TValue> {
  encode(value: TValue): unknown;
  decode(raw: unknown): TValue;
}

export interface SlotsCodec<TValue extends object> extends Codec<TValue> {
  readonly members: readonly CultCacheSchemaCatalogMember[];
  readonly names: readonly string[];
}

function expect(raw: unknown, what: string, ok: boolean): asserts ok {
  if (!ok) {
    throw new Error(`expected ${what}, found ${raw === null ? "nil" : Array.isArray(raw) ? "array" : typeof raw}`);
  }
}

export const string: Codec<string> = {
  encode: (value) => (expect(value, "a string", typeof value === "string"), value),
  decode: (raw) => (expect(raw, "a string", typeof raw === "string"), raw as string),
};

export const number: Codec<number> = {
  encode: (value) => (expect(value, "a number", typeof value === "number" && Number.isFinite(value)), value),
  decode: (raw) => (expect(raw, "a number", typeof raw === "number" && Number.isFinite(raw)), raw as number),
};

export const boolean: Codec<boolean> = {
  encode: (value) => (expect(value, "a boolean", typeof value === "boolean"), value),
  decode: (raw) => (expect(raw, "a boolean", typeof raw === "boolean"), raw as boolean),
};

export const bytes: Codec<Uint8Array> = {
  encode: (value) => (expect(value, "bytes", value instanceof Uint8Array), value),
  decode: (raw) => (expect(raw, "bytes", raw instanceof Uint8Array), raw as Uint8Array),
};

export function list<TItem>(item: Codec<TItem>): Codec<TItem[]> {
  return {
    encode(value) {
      expect(value, "an array", Array.isArray(value));
      return value.map((element, index) => at(`element ${index}`, () => item.encode(element)));
    },
    decode(raw) {
      expect(raw, "an array", Array.isArray(raw));
      return (raw as unknown[]).map((element, index) => at(`element ${index}`, () => item.decode(element)));
    },
  };
}

export function nullable<TValue>(inner: Codec<TValue>): Codec<TValue | null> {
  return {
    encode: (value) => (value === null || value === undefined ? null : inner.encode(value)),
    decode: (raw) => (raw === null ? null : inner.decode(raw)),
  };
}

export function lazy<TValue>(get: () => Codec<TValue>): Codec<TValue> {
  let resolved: Codec<TValue> | undefined;
  const codec = () => (resolved ??= get());
  return {
    encode: (value) => codec().encode(value),
    decode: (raw) => codec().decode(raw),
  };
}

// Prefixes a refusal with where it happened, so a nested refusal still names its field and its union.
function at<TResult>(where: string, run: () => TResult): TResult {
  try {
    return run();
  } catch (error) {
    throw new Error(`${where}: ${(error as Error).message}`);
  }
}

type SlotSpec<TValue, TName extends string = string> =
  | readonly [name: TName, codec: Codec<TValue>]
  | readonly [name: TName, codec: Codec<TValue>, defaultValue: TValue | (() => TValue)];

type SlotsSpec<TValue> = Readonly<Record<number, { [K in keyof TValue & string]: SlotSpec<TValue[K], K> }[keyof TValue & string]>>;

function slotKeys(spec: Readonly<Record<string, unknown>>, owner: string): number[] {
  const keys = Object.keys(spec).map((text) => {
    const key = Number(text);
    if (!Number.isInteger(key) || key < 0 || String(key) !== text) {
      throw new Error(`${owner} declares the key "${text}", which is not a non-negative integer.`);
    }
    return key;
  });
  return keys.sort((left, right) => left - right);
}

function pascalCase(name: string): string {
  return name
    .split(/[^A-Za-z0-9]+/u)
    .filter((part) => part.length > 0)
    .map((part) => part[0].toUpperCase() + part.slice(1))
    .join("");
}

// A positional record: slot `key` holds field `name`. Gaps are written nil and skipped on read, extra trailing slots
// are skipped, and a slot missing from the payload takes its declared default or is refused. A nil in a slot that
// declares a default also takes the default (cultcache-py's rule).
export function slots<TValue extends object>(spec: SlotsSpec<TValue>): SlotsCodec<TValue> {
  const keys = slotKeys(spec, "A slot record");
  if (keys.length === 0) {
    throw new Error("A slot record declares no slots.");
  }
  const fields = keys.map((key) => {
    const [name, codec, ...rest] = (spec as unknown as Readonly<Record<number, SlotSpec<unknown>>>)[key];
    return { key, name, codec, hasDefault: rest.length > 0, defaultValue: rest[0] };
  });
  const seen = new Set<string>();
  for (const field of fields) {
    if (seen.has(field.name)) {
      throw new Error(`A slot record declares the field "${field.name}" twice.`);
    }
    seen.add(field.name);
  }
  const length = keys[keys.length - 1] + 1;
  const defaultOf = (field: (typeof fields)[number]): unknown =>
    typeof field.defaultValue === "function" ? (field.defaultValue as () => unknown)() : field.defaultValue;

  return {
    names: fields.map((field) => field.name),
    members: fields.map((field) => ({ slot: field.key, memberName: pascalCase(field.name), typeName: "System.Object" })),
    encode(value) {
      expect(value, "a record", typeof value === "object" && value !== null);
      const out: unknown[] = new Array(length).fill(null);
      for (const field of fields) {
        out[field.key] = at(`${field.name} (slot ${field.key})`, () =>
          field.codec.encode((value as Record<string, unknown>)[field.name]));
      }
      return out;
    },
    decode(raw) {
      expect(raw, "an array of slots", Array.isArray(raw));
      const input = raw as unknown[];
      const out: Record<string, unknown> = {};
      for (const field of fields) {
        const present = field.key < input.length && !(input[field.key] === null && field.hasDefault);
        if (!present && !field.hasDefault) {
          throw new Error(`${field.name} (slot ${field.key}): the slot is missing and declares no default.`);
        }
        out[field.name] = present
          ? at(`${field.name} (slot ${field.key})`, () => field.codec.decode(input[field.key]))
          : defaultOf(field);
      }
      return out as TValue;
    },
  };
}

// An arm with no slots. It is written [key, []], never [key, nil].
export interface UnitArm {
  readonly unit: true;
}
export const unit: UnitArm = Object.freeze({ unit: true });

type ArmSpec<TKind extends string> = readonly [armName: TKind, body: SlotsCodec<any> | UnitArm];

// A tagged union written [armKey, [armSlots...]]. The reader refuses an undeclared key, naming the union, the key and
// the declared keys; there is no fallback arm.
export function union<TValue extends { kind: string }>(
  name: string,
  arms: Readonly<Record<number, ArmSpec<TValue["kind"]>>>,
): Codec<TValue | null> {
  const keys = slotKeys(arms, `Union ${name}`);
  if (keys.length === 0) {
    throw new Error(`Union ${name} declares no arms.`);
  }
  const byKey = new Map<number, { armName: string; body: SlotsCodec<any> | UnitArm }>();
  const byName = new Map<string, number>();
  for (const key of keys) {
    const [armName, body] = arms[key];
    if (byName.has(armName)) {
      throw new Error(`Union ${name} declares the arm "${armName}" twice.`);
    }
    if (body !== unit && (body as SlotsCodec<any>).names.includes("kind")) {
      throw new Error(`Union ${name} arm "${armName}" declares a slot named kind, which is the union's own tag.`);
    }
    byKey.set(key, { armName, body });
    byName.set(armName, key);
  }
  const declared = keys.join(", ");

  return {
    encode(value) {
      if (value === null || value === undefined) {
        return null;
      }
      const key = byName.get((value as { kind?: string }).kind as string);
      if (key === undefined) {
        throw new Error(`Union ${name}: the value's kind is not one of its arms (${[...byName.keys()].join(", ")}).`);
      }
      const { armName, body } = byKey.get(key)!;
      return [key, body === unit ? [] : at(`arm ${armName}`, () => (body as SlotsCodec<any>).encode(value))];
    },
    decode(raw) {
      if (raw === null) {
        return null;
      }
      if (!Array.isArray(raw) || raw.length !== 2 || !Array.isArray(raw[1])) {
        throw new Error(`Union ${name}: expected [key, slots] and found something else.`);
      }
      const key: unknown = raw[0];
      if (typeof key !== "number" || !Number.isInteger(key)) {
        throw new Error(`Union ${name}: the arm key is not an integer (declared keys: ${declared}).`);
      }
      const arm = byKey.get(key);
      if (arm === undefined) {
        throw new Error(`Union ${name}: unknown arm key ${key} (declared keys: ${declared}).`);
      }
      const fields = arm.body === unit ? {} : at(`arm ${arm.armName}`, () => (arm.body as SlotsCodec<any>).decode(raw[1]));
      return { kind: arm.armName, ...fields } as unknown as TValue;
    },
  };
}

// The formatter a document gets from a slot codec: the codec's value as one MessagePack payload.
export function codecFormatter<TValue extends object>(codec: SlotsCodec<TValue>): CultCacheDocumentFormatter<TValue> {
  return {
    encode: (value) => encode(codec.encode(value)),
    decode: (payload) => codec.decode(decode(payload)),
  };
}
