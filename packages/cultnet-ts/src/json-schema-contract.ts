import Ajv2020, { type ErrorObject, type ValidateFunction } from "ajv/dist/2020";
import type { CultCacheSchema } from "cultcache-ts";

export interface JsonSchemaContract<TValue> extends CultCacheSchema<TValue> {
  readonly schemaId: string;
  readonly schema: object;
  readonly validate: (input: unknown) => input is TValue;
}

export interface JsonSchemaContractDefinition {
  schemaId: string;
  schema: object;
}

const ajv = new Ajv2020({
  allErrors: true,
  strict: false,
});

export function defineJsonSchemaContract<TValue>(
  definition: JsonSchemaContractDefinition,
): JsonSchemaContract<TValue> {
  const validator = ajv.compile(definition.schema);

  return Object.freeze({
    schemaId: definition.schemaId,
    schema: definition.schema,
    validate(input: unknown): input is TValue {
      return validator(input) as boolean;
    },
    parse(input: unknown): TValue {
      if (!validator(input)) {
        throw new Error(renderValidationErrors(definition.schemaId, validator));
      }

      return input as TValue;
    },
  });
}

function renderValidationErrors(schemaId: string, validator: ValidateFunction): string {
  const details = validator.errors?.map(renderError) ?? ["unknown validation failure"];
  return `Validation failed for ${schemaId}: ${details.join("; ")}`;
}

function renderError(error: ErrorObject): string {
  const location = error.instancePath.length > 0 ? error.instancePath : "/";
  return `${location}: ${error.message}`;
}
