#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameCult.Networking.Tests
{
    /// <summary>
    /// A deliberately small JSON Schema (draft 2020-12) validator covering only the constructs
    /// contracts/cultnet/*.schema.json actually use: object/array/string/integer/boolean/null types
    /// (plain or as a ["type","null"] union), required, additionalProperties: false, properties,
    /// items, const, enum, pattern, minLength, maxLength, minItems, maxItems, minimum, maximum, oneOf,
    /// not, and $ref - same-document ("#/$defs/name"), cross-document ("other.schema.json"), and both
    /// ("other.schema.json#/$defs/name"). Not a general validator: it exists for R-S
    /// (docs/cultnet-selection-cut.md, fix batch 3) to decode real C# wire bytes and check them against
    /// the committed schema files, which no dependency in this repo already does - see
    /// CultNetSchemaContractTests.
    /// R-X (fix batch 4): a schema keyword this validator does not implement is refused, not silently
    /// ignored - see <see cref="ImplementedKeywords"/>/<see cref="MetadataKeywords"/> and
    /// <see cref="CheckSupportedKeywords"/>. Before this, a schema using e.g. allOf/anyOf/if-then-else/
    /// uniqueItems validated an instance as if that keyword were not there at all, which is silent
    /// under-validation, not the "no such constraint" the schema author intended.
    /// </summary>
    internal sealed class MiniJsonSchemaValidator
    {
        // Keywords this validator gives real constraint semantics to.
        private static readonly HashSet<string> ImplementedKeywords = new(StringComparer.Ordinal)
        {
            "$ref", "oneOf", "not", "const", "enum", "type",
            "minLength", "maxLength", "pattern", "minItems", "maxItems", "minimum", "maximum",
            "items", "required", "properties", "additionalProperties"
        };

        // Keywords that are pure annotation/metadata - present on many schema nodes, never checked
        // against an instance by any JSON Schema vocabulary this validator implements, so there is
        // nothing for CheckSupportedKeywords to refuse. contentEncoding/contentMediaType/contentSchema
        // are "media" annotations that draft 2020-12 itself does not assign validation behavior to
        // without an explicit vocabulary; x-cult-runtime-type is this repo's own vendor extension.
        private static readonly HashSet<string> MetadataKeywords = new(StringComparer.Ordinal)
        {
            "$schema", "$id", "$defs", "title", "description", "default",
            "contentEncoding", "x-cult-runtime-type"
        };

        private readonly string _schemaDir;
        private readonly Dictionary<string, JsonElement> _docCache = new(StringComparer.Ordinal);

        public MiniJsonSchemaValidator(string schemaDir)
        {
            _schemaDir = schemaDir;
        }

        /// <summary>Validates <paramref name="instance"/> against the named schema file's root schema. Throws with every collected error when invalid.</summary>
        public void AssertValid(JsonElement instance, string schemaFileName)
        {
            var errors = Validate(instance, schemaFileName);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{instance} does not satisfy {schemaFileName}:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
            }
        }

        /// <summary>Validates against a $defs entry of the named schema file, e.g. "cultnet.selection.schema.json#/$defs/fieldPredicate".</summary>
        public List<string> Validate(JsonElement instance, string refText)
        {
            var errors = new List<string>();
            var (schema, file) = Resolve(refText, currentFile: "");
            ValidateAgainst(instance, schema, file, "$", errors);
            return errors;
        }

        private JsonElement LoadDoc(string fileName)
        {
            if (_docCache.TryGetValue(fileName, out var cached)) return cached;
            var path = Path.Combine(_schemaDir, fileName);
            var doc = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            _docCache[fileName] = doc;
            return doc;
        }

        private (JsonElement Schema, string File) Resolve(string refText, string currentFile)
        {
            var hash = refText.IndexOf('#');
            string file;
            string pointer;
            if (hash < 0)
            {
                file = refText;
                pointer = "";
            }
            else
            {
                var filePart = refText[..hash];
                file = filePart.Length == 0 ? currentFile : filePart;
                pointer = refText[(hash + 1)..];
            }

            var doc = LoadDoc(file);
            if (string.IsNullOrEmpty(pointer) || pointer == "/") return (doc, file);

            var current = doc;
            foreach (var segment in pointer.TrimStart('/').Split('/'))
            {
                var key = segment.Replace("~1", "/").Replace("~0", "~");
                current = current.GetProperty(key);
            }

            return (current, file);
        }

        private void ValidateAgainst(JsonElement instance, JsonElement schema, string file, string path, List<string> errors)
        {
            CheckSupportedKeywords(schema, path, errors);

            if (schema.TryGetProperty("$ref", out var refProp))
            {
                var (resolved, resolvedFile) = Resolve(refProp.GetString()!, file);
                ValidateAgainst(instance, resolved, resolvedFile, path, errors);
                return;
            }

            if (schema.TryGetProperty("oneOf", out var oneOf))
            {
                var matches = 0;
                var branchErrors = new List<string>();
                foreach (var branch in oneOf.EnumerateArray())
                {
                    var local = new List<string>();
                    ValidateAgainst(instance, branch, file, path, local);
                    if (local.Count == 0) matches++;
                    else branchErrors.AddRange(local);
                }

                if (matches != 1)
                {
                    errors.Add($"{path}: oneOf matched {matches} branches (want exactly 1); branch errors: {string.Join(" | ", branchErrors)}");
                }
            }

            if (schema.TryGetProperty("not", out var not))
            {
                var local = new List<string>();
                ValidateAgainst(instance, not, file, path, local);
                if (local.Count == 0)
                    errors.Add($"{path}: matched the \"not\" subschema, which it must not");
            }

            if (schema.TryGetProperty("const", out var constEl))
            {
                if (!JsonElementDeepEquals(instance, constEl))
                    errors.Add($"{path}: expected const {constEl}, got {instance}");
            }

            if (schema.TryGetProperty("enum", out var enumEl))
            {
                if (!enumEl.EnumerateArray().Any(candidate => JsonElementDeepEquals(instance, candidate)))
                    errors.Add($"{path}: value {instance} is not one of the declared enum values");
            }

            if (schema.TryGetProperty("type", out var typeEl))
            {
                var allowed = typeEl.ValueKind == JsonValueKind.Array
                    ? typeEl.EnumerateArray().Select(t => t.GetString()!).ToArray()
                    : new[] { typeEl.GetString()! };
                if (!allowed.Any(t => MatchesType(instance, t)))
                    errors.Add($"{path}: kind {instance.ValueKind} does not satisfy type [{string.Join(",", allowed)}]");
            }

            // Null short-circuits the value-shaped checks below (a nilled key still satisfies a
            // ["...","null"] type, but has no length/properties/items of its own to check).
            if (instance.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            if (instance.ValueKind == JsonValueKind.String)
            {
                var value = instance.GetString()!;
                if (schema.TryGetProperty("minLength", out var minLength) && value.Length < minLength.GetInt32())
                    errors.Add($"{path}: string shorter than minLength {minLength.GetInt32()}");
                if (schema.TryGetProperty("maxLength", out var maxLength) && value.Length > maxLength.GetInt32())
                    errors.Add($"{path}: string longer than maxLength {maxLength.GetInt32()}");
                if (schema.TryGetProperty("pattern", out var pattern))
                {
                    // R-X: a full match, not Regex.IsMatch's "search anywhere" - and not .NET's own `$`,
                    // which (unlike ECMA-262's) matches immediately before a single trailing newline even
                    // without RegexOptions.Multiline, so an author-anchored "^...$" pattern here would
                    // wrongly accept e.g. "5\n" for a pattern that means "exactly one digit".
                    var match = Regex.Match(value, pattern.GetString()!);
                    if (!match.Success || match.Index != 0 || match.Length != value.Length)
                        errors.Add($"{path}: \"{value}\" does not match pattern {pattern.GetString()}");
                }
            }

            if (instance.ValueKind == JsonValueKind.Number)
            {
                if (schema.TryGetProperty("minimum", out var minimum) && instance.GetDouble() < minimum.GetDouble())
                    errors.Add($"{path}: {instance} is less than minimum {minimum}");
                if (schema.TryGetProperty("maximum", out var maximum) && instance.GetDouble() > maximum.GetDouble())
                    errors.Add($"{path}: {instance} is greater than maximum {maximum}");
            }

            if (instance.ValueKind == JsonValueKind.Array)
            {
                var items = instance.EnumerateArray().ToArray();
                if (schema.TryGetProperty("minItems", out var minItems) && items.Length < minItems.GetInt32())
                    errors.Add($"{path}: array shorter than minItems {minItems.GetInt32()}");
                if (schema.TryGetProperty("maxItems", out var maxItems) && items.Length > maxItems.GetInt32())
                    errors.Add($"{path}: array longer than maxItems {maxItems.GetInt32()}");
                if (schema.TryGetProperty("items", out var itemSchema))
                {
                    for (var i = 0; i < items.Length; i++)
                        ValidateAgainst(items[i], itemSchema, file, $"{path}[{i}]", errors);
                }
            }

            if (instance.ValueKind == JsonValueKind.Object)
            {
                var propertyNames = instance.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                if (schema.TryGetProperty("required", out var required))
                {
                    foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
                    {
                        if (!propertyNames.Contains(name))
                            errors.Add($"{path}: missing required property \"{name}\"");
                    }
                }

                var declared = schema.TryGetProperty("properties", out var properties)
                    ? properties.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                if (schema.TryGetProperty("additionalProperties", out var additional) &&
                    additional.ValueKind == JsonValueKind.False)
                {
                    foreach (var name in propertyNames)
                    {
                        if (!declared.Contains(name))
                            errors.Add($"{path}: property \"{name}\" is not declared and additionalProperties is false");
                    }
                }

                if (properties.ValueKind == JsonValueKind.Object)
                {
                    foreach (var propertySchema in properties.EnumerateObject())
                    {
                        if (instance.TryGetProperty(propertySchema.Name, out var propertyValue))
                            ValidateAgainst(propertyValue, propertySchema.Value, file, $"{path}.{propertySchema.Name}", errors);
                    }
                }
            }
        }

        /// <summary>
        /// R-X: refuses a schema node that uses a keyword this validator gives no behaviour to - allOf,
        /// anyOf, if/then/else and uniqueItems are the ones found in the committed contracts at the time
        /// this check was added. Before this, such a keyword was silently invisible: an instance that
        /// violated it still validated clean, because nothing ever looked at the keyword to begin with.
        /// Only checks the schema node actually reached during this validation (the same nodes
        /// ValidateAgainst recurses into) - it is not a whole-document schema linter.
        /// </summary>
        private static void CheckSupportedKeywords(JsonElement schema, string path, List<string> errors)
        {
            if (schema.ValueKind != JsonValueKind.Object) return;
            foreach (var property in schema.EnumerateObject())
            {
                if (!ImplementedKeywords.Contains(property.Name) && !MetadataKeywords.Contains(property.Name))
                    errors.Add($"{path}: schema uses keyword \"{property.Name}\", which this validator does not implement");
            }
        }

        private static bool MatchesType(JsonElement instance, string type) => type switch
        {
            "object" => instance.ValueKind == JsonValueKind.Object,
            "array" => instance.ValueKind == JsonValueKind.Array,
            "string" => instance.ValueKind == JsonValueKind.String,
            "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => instance.ValueKind == JsonValueKind.Null,
            "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
            "number" => instance.ValueKind == JsonValueKind.Number,
            _ => throw new NotSupportedException($"Unsupported schema type \"{type}\".")
        };

        private static bool JsonElementDeepEquals(JsonElement a, JsonElement b)
        {
            if (a.ValueKind != b.ValueKind) return false;
            return a.ValueKind switch
            {
                JsonValueKind.String => a.GetString() == b.GetString(),
                JsonValueKind.Number => a.GetRawText() == b.GetRawText(),
                JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
                _ => a.GetRawText() == b.GetRawText()
            };
        }
    }
}
