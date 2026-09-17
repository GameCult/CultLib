#nullable enable
namespace GameCult.Caching
{
    // The Addressables key form a [CultInspectorAssetGuid] member stores: the bare GUID for a main
    // asset, or "guid[subAssetName]" when a sub-asset (one sprite of a sheet, say) is picked. This is
    // Addressables' own key shape (what AssetReference keeps as GUID plus sub-object name); engine-free
    // so both the Studio drawer and any headless check can pin the same parse without touching Unity.
    public static class CultAssetGuidKey
    {
        public static bool TryParse(string key, out string guid, out string? subAssetName)
        {
            guid = string.Empty;
            subAssetName = null;
            if (string.IsNullOrEmpty(key)) return false;

            var bracket = key.IndexOf('[');
            if (bracket < 0)
            {
                guid = key;
                return true;
            }

            if (bracket == 0 || key[key.Length - 1] != ']') return false;

            var name = key.Substring(bracket + 1, key.Length - bracket - 2);
            if (name.Length == 0) return false;

            guid = key.Substring(0, bracket);
            subAssetName = name;
            return true;
        }

        public static string Format(string guid, string? subAssetName)
        {
            guid ??= string.Empty;
            return string.IsNullOrEmpty(subAssetName) ? guid : guid + "[" + subAssetName + "]";
        }
    }
}
