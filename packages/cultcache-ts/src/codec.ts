import type { CultCacheDocumentFormatter, CultCacheSchemaCatalogMember } from "./types";
import { readWire, WireFloat, writeWire } from "./wire";

// A codec turns a value into the shape the reference runtimes write and back. Slot records are positional arrays with
// nil gaps; a union value is { kind: armName, ...armFields }, written [armKey, [armSlots...]]. Refusals name the union
// or the slot and the key or code, and never echo a decoded value. `typeName` is the reference's name for the type
// (CultSchemaTypeNames.FromType): a store's schema catalog carries it per slot, and the reference refuses a store whose
// slot names another type. The leaves know theirs; a union or a record the codec cannot name from C# is given it.
export interface Codec<TValue> {
  encode(value: TValue): unknown;
  decode(raw: unknown): TValue;
  readonly typeName?: string;
  // True for a type the reference makes Nullable<T> when the slot may be nil (int, long, float, double, bool).
  readonly valueType?: boolean;
}

export interface SlotsCodec<TValue extends object> extends Codec<TValue> {
  // Throws when a slot's codec has no typeName: the catalog cannot be derived without the reference's names.
  readonly members: readonly CultCacheSchemaCatalogMember[];
  readonly names: readonly string[];
}

function kindOf(raw: unknown): string {
  if (raw === null || raw === undefined) return "nil";
  if (Array.isArray(raw)) return "array";
  if (raw instanceof WireFloat) return "float";
  if (typeof raw === "bigint") return "integer";
  return typeof raw;
}

function refuse(what: string, found: string): never {
  throw new Error(`expected ${what}, found ${found}`);
}

function expect(raw: unknown, what: string, ok: boolean): asserts ok {
  if (!ok) refuse(what, kindOf(raw));
}

export const string: Codec<string> = {
  typeName: "System.String",
  encode: (value) => (expect(value, "a string", typeof value === "string"), value),
  decode: (raw) => (expect(raw, "a string", typeof raw === "string"), raw as string),
};

export const boolean: Codec<boolean> = {
  typeName: "System.Boolean",
  valueType: true,
  encode: (value) => (expect(value, "a boolean", typeof value === "boolean"), value),
  decode: (raw) => (expect(raw, "a boolean", typeof raw === "boolean"), raw as boolean),
};

export const bytes: Codec<Uint8Array> = {
  typeName: "System.Byte[]",
  encode: (value) => (expect(value, "bytes", value instanceof Uint8Array), value),
  decode: (raw) => (expect(raw, "bytes", raw instanceof Uint8Array), raw as Uint8Array),
};

// A C# integer slot: written as the smallest MessagePack integer and read from any integer encoding in range. A float is
// refused even when it holds a whole number, as the reference refuses it.
function integerCodec(typeName: string, what: string, low: bigint, high: bigint): Codec<number | bigint> {
  const inRange = (value: number | bigint) => value >= low && value <= high;
  return {
    typeName,
    valueType: true,
    encode(value) {
      if (typeof value === "bigint") {
        if (!inRange(value)) refuse(what, "an integer out of range");
        return value;
      }
      if (typeof value !== "number") refuse(what, kindOf(value));
      if (!Number.isSafeInteger(value)) {
        refuse(what, Number.isInteger(value) ? "a number beyond 2^53, which needs a bigint" : "a non-integer number");
      }
      if (!inRange(value)) refuse(what, "an integer out of range");
      return value;
    },
    decode(raw) {
      if (typeof raw !== "number" && typeof raw !== "bigint") refuse(what, kindOf(raw));
      if (!inRange(raw)) refuse(what, "an integer out of range");
      return raw;
    },
  };
}

// C# int. Always a number.
export const int32 = integerCodec("System.Int32", "an int32", -(2n ** 31n), 2n ** 31n - 1n) as Codec<number>;
// C# long. A number when the value is a safe integer, a bigint beyond 2^53 (so 64-bit values never lose precision).
export const int64 = integerCodec("System.Int64", "an int64", -(2n ** 63n), 2n ** 63n - 1n);

// A 64-bit integer to the nearest float32 in one rounding, ties to even, as the reference's cast does. Number(value)
// first would round to a double and then again, and the two roundings can disagree.
function bigintToFloat32(value: bigint): number {
  const magnitude = value < 0n ? -value : value;
  const excess = magnitude.toString(2).length - 24;
  if (excess <= 0) return Number(value);
  const kept = magnitude >> BigInt(excess);
  const rest = magnitude - (kept << BigInt(excess));
  const half = 1n << BigInt(excess - 1);
  const rounded = Number(rest > half || (rest === half && (kept & 1n) === 1n) ? kept + 1n : kept) * 2 ** excess;
  return value < 0n ? -rounded : rounded;
}

function floatCodec(typeName: string, wide: boolean): Codec<number> {
  const what = wide ? "a float64" : "a float32";
  return {
    typeName,
    valueType: true,
    encode(value) {
      expect(value, what, typeof value === "number");
      return new WireFloat(value, wide);
    },
    decode(raw) {
      if (raw instanceof WireFloat) return wide ? raw.value : Math.fround(raw.value);
      if (typeof raw === "number") return wide ? raw : Math.fround(raw);
      if (typeof raw === "bigint") return wide ? Number(raw) : bigintToFloat32(raw);
      return refuse(what, kindOf(raw));
    },
  };
}

// C# float, written as MessagePack float 32.
export const float32 = floatCodec("System.Single", false);
// C# double, written as MessagePack float 64.
export const float64 = floatCodec("System.Double", true);

function sequence<TItem>(item: Codec<TItem>, nameOf: (itemName: string) => string): Codec<TItem[]> {
  return {
    get typeName() { return item.typeName === undefined ? undefined : nameOf(item.typeName); },
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

// C# List<T>.
export function list<TItem>(item: Codec<TItem>): Codec<TItem[]> {
  return sequence(item, (name) => `System.Collections.Generic.List<${name}>`);
}

// C# T[]. The same wire as list.
export function array<TItem>(item: Codec<TItem>): Codec<TItem[]> {
  return sequence(item, (name) => `${name}[]`);
}

// A slot that may be nil. For a value type the reference's name is Nullable<T>; for a reference type it is unchanged.
export function nullable<TValue>(inner: Codec<TValue>): Codec<TValue | null> {
  return {
    get typeName() {
      return inner.typeName !== undefined && inner.valueType ? `System.Nullable<${inner.typeName}>` : inner.typeName;
    },
    encode: (value) => (value === null || value === undefined ? null : inner.encode(value)),
    decode: (raw) => (raw === null ? null : inner.decode(raw)),
  };
}

export function lazy<TValue>(get: () => Codec<TValue>): Codec<TValue> {
  let resolved: Codec<TValue> | undefined;
  const codec = () => (resolved ??= get());
  return {
    get typeName() { return codec().typeName; },
    get valueType() { return codec().valueType; },
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

const INT32_MIN = -(2 ** 31);
const INT32_MAX = 2 ** 31 - 1;

// The declared keys, ascending. The reference's keys are Int32: a union arm may be any (a negative one too), a slot is
// non-negative, and a key outside the range is refused at definition rather than allocated for.
function slotKeys(spec: Readonly<Record<string, unknown>>, owner: string, low: number): number[] {
  return Object.keys(spec)
    .map((text) => {
      if (!/^-?(0|[1-9][0-9]*)$/u.test(text) || (low >= 0 && text.startsWith("-"))) {
        throw new Error(`${owner} declares the key "${text}", which is not ${low < 0 ? "an" : "a non-negative"} integer.`);
      }
      const key = BigInt(text);
      if (key < BigInt(low) || key > BigInt(INT32_MAX)) {
        throw new Error(`${owner} declares the key ${text}, which is outside Int32.`);
      }
      return Number(key);
    })
    // Object.keys lists negative keys after the others, in the order they were written: sort them numerically.
    .sort((left, right) => left - right);
}

function pascalCase(name: string): string {
  return name
    .split(/[^A-Za-z0-9]/u)
    .filter((part) => part.length > 0)
    .map((part) => part[0].toUpperCase() + part.slice(1))
    .join("");
}

// A positional record: slot `key` holds field `name`. Gaps are written nil and skipped on read, extra trailing slots
// are skipped, and a slot missing from the payload takes its declared default or is refused. A nil in a slot that
// declares a default also takes the default (cultcache-py's rule).
export function slots<TValue extends object>(spec: SlotsSpec<TValue>, typeName?: string): SlotsCodec<TValue> {
  const keys = slotKeys(spec, "A slot record", 0);
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
    typeName,
    get members() {
      return fields.map((field) => {
        if (field.codec.typeName === undefined) {
          throw new Error(`${field.name} (slot ${field.key}) has no C# type name for the schema catalog; give its union or record a typeName, or give the document its members.`);
        }
        return { slot: field.key, memberName: pascalCase(field.name), typeName: field.codec.typeName };
      });
    },
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
  typeName?: string,
): Codec<TValue | null> {
  const keys = slotKeys(arms, `Union ${name}`, INT32_MIN);
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
    typeName,
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
      if (typeof key !== "number" && typeof key !== "bigint") {
        throw new Error(`Union ${name}: the arm key is not an integer (declared keys: ${declared}).`);
      }
      if (key < INT32_MIN || key > INT32_MAX) {
        throw new Error(`Union ${name}: the arm key is outside Int32 (declared keys: ${declared}).`);
      }
      const arm = byKey.get(Number(key));
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
    encode: (value) => writeWire(codec.encode(value)),
    decode: (payload) => codec.decode(readWire(payload)),
  };
}
