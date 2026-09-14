using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameCult.Caching;
using MessagePack;
using UnityEditor;
using UnityEngine;
using CM = CultMath;
using Object = UnityEngine.Object;

namespace GameCult.Unity.Caching.Editor
{
    // Returns the member's next value; mutate reference values in place or return a new one.
    public interface ICultInspectorDrawer
    {
        object Draw(string label, object value, MemberInfo member);
    }

    internal sealed class CultInspector
    {
        private static Dictionary<Type, ICultInspectorDrawer> _drawers;
        private static readonly Dictionary<Type, MemberInfo> NameMembers = new Dictionary<Type, MemberInfo>();
        private static GUIStyle _errorStyle;

        private readonly CultCache _cache;
        private readonly Dictionary<string, bool> _foldouts = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<Type, MemberInfo[]> _members = new Dictionary<Type, MemberInfo[]>();

        public CultInspector(CultCache cache)
        {
            _cache = cache;
        }

        public CultStoredDocument[] Records { get; set; } = Array.Empty<CultStoredDocument>();

        private static GUIStyle ErrorStyle => _errorStyle ??= new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(1f, .35f, .3f) } };

        private static Dictionary<Type, ICultInspectorDrawer> Drawers
        {
            get
            {
                if (_drawers != null) return _drawers;
                _drawers = new Dictionary<Type, ICultInspectorDrawer>();
                foreach (var type in TypeCache.GetTypesWithAttribute<CultInspectorDrawerAttribute>())
                {
                    var memberType = type.GetCustomAttribute<CultInspectorDrawerAttribute>().MemberType;
                    if (!typeof(ICultInspectorDrawer).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null)
                        Debug.LogError($"{type.FullName} is marked [CultInspectorDrawer] but is not a concrete ICultInspectorDrawer with a parameterless constructor.");
                    else if (_drawers.ContainsKey(memberType))
                        Debug.LogError($"{type.FullName} and {_drawers[memberType].GetType().FullName} both draw {memberType.FullName}.");
                    else
                        _drawers[memberType] = (ICultInspectorDrawer)Activator.CreateInstance(type);
                }

                return _drawers;
            }
        }

        public static string RecordLabel(CultStoredDocument record)
        {
            var type = record.Descriptor.DocumentType;
            if (!NameMembers.TryGetValue(type, out var member))
            {
                member = record.Descriptor.NameMember == null
                    ? null
                    : type.GetMember(record.Descriptor.NameMember, BindingFlags.Public | BindingFlags.Instance).FirstOrDefault();
                NameMembers[type] = member;
            }

            var name = member == null ? null : Get(member, record.Document)?.ToString();
            return string.IsNullOrWhiteSpace(name) ? record.Key.Value : name;
        }

        public void DrawDocument(CultStoredDocument record)
        {
            DrawMembers(record.Document, record.Descriptor.DocumentType, record.Key.Value);
        }

        public object DrawValue(string label, Type type, object value, MemberInfo member, string path)
        {
            if (Drawers.TryGetValue(type, out var drawer) || type.IsGenericType && Drawers.TryGetValue(type.GetGenericTypeDefinition(), out drawer))
                return drawer.Draw(label, value, member);

            if (type == typeof(string)) return DrawString(label, value as string, member);
            var range = member?.GetCustomAttribute<CultInspectorRangeAttribute>();
            if (type == typeof(int))
                return range == null
                    ? EditorGUILayout.IntField(label, As<int>(value))
                    : EditorGUILayout.IntSlider(label, As<int>(value), Mathf.RoundToInt(range.Min), Mathf.RoundToInt(range.Max));
            if (type == typeof(float))
                return range == null
                    ? EditorGUILayout.FloatField(label, As<float>(value))
                    : EditorGUILayout.Slider(label, As<float>(value), range.Min, range.Max);
            if (type == typeof(bool)) return EditorGUILayout.Toggle(label, As<bool>(value));
            if (type == typeof(double)) return EditorGUILayout.DoubleField(label, As<double>(value));
            if (type == typeof(long)) return EditorGUILayout.LongField(label, As<long>(value));
            if (type == typeof(uint)) return (uint)Math.Min(uint.MaxValue, Math.Max(0L, EditorGUILayout.LongField(label, As<uint>(value))));
            if (type == typeof(short)) return (short)Mathf.Clamp(EditorGUILayout.IntField(label, As<short>(value)), short.MinValue, short.MaxValue);
            if (type == typeof(byte)) return (byte)Mathf.Clamp(EditorGUILayout.IntField(label, As<byte>(value)), 0, 255);
            if (type.IsEnum)
            {
                var current = value as Enum ?? (Enum)Enum.ToObject(type, 0);
                return type.IsDefined(typeof(FlagsAttribute), false)
                    ? EditorGUILayout.EnumFlagsField(label, current)
                    : EditorGUILayout.EnumPopup(label, current);
            }

            if (type == typeof(CM.float2)) { var v = As<CM.float2>(value); var r = EditorGUILayout.Vector2Field(label, new Vector2(v.x, v.y)); return new CM.float2(r.x, r.y); }
            if (type == typeof(CM.float3)) { var v = As<CM.float3>(value); var r = EditorGUILayout.Vector3Field(label, new Vector3(v.x, v.y, v.z)); return new CM.float3(r.x, r.y, r.z); }
            if (type == typeof(CM.float4)) { var v = As<CM.float4>(value); var r = EditorGUILayout.Vector4Field(label, new Vector4(v.x, v.y, v.z, v.w)); return new CM.float4(r.x, r.y, r.z, r.w); }
            if (type == typeof(CM.quaternion)) { var v = As<CM.quaternion>(value); var r = EditorGUILayout.Vector4Field(label, new Vector4(v.x, v.y, v.z, v.w)); return new CM.quaternion(r.x, r.y, r.z, r.w); }
            if (type == typeof(CM.int2)) { var v = As<CM.int2>(value); var r = EditorGUILayout.Vector2IntField(label, new Vector2Int(v.x, v.y)); return new CM.int2(r.x, r.y); }
            if (type == typeof(CM.double2)) { var v = As<CM.double2>(value); var r = DrawDoubles(label, v.x, v.y); return new CM.double2(r[0], r[1]); }
            if (type == typeof(CM.double3)) { var v = As<CM.double3>(value); var r = DrawDoubles(label, v.x, v.y, v.z); return new CM.double3(r[0], r[1], r[2]); }
            if (type == typeof(CM.bool2)) return DrawBool2(label, As<CM.bool2>(value));
            if (type == typeof(CM.rect))
            {
                var v = As<CM.rect>(value);
                var r = EditorGUILayout.RectField(label, Rect.MinMaxRect(v.min.x, v.min.y, v.max.x, v.max.y));
                return new CM.rect(r.xMin, r.yMin, r.xMax, r.yMax);
            }

            if (type == typeof(CM.Color32))
            {
                var v = As<CM.Color32>(value);
                Color32 c = EditorGUILayout.ColorField(label, new Color32(v.r, v.g, v.b, v.a));
                return new CM.Color32(c.r, c.g, c.b, c.a);
            }

            if (type == typeof(Vector2)) return EditorGUILayout.Vector2Field(label, As<Vector2>(value));
            if (type == typeof(Vector3)) return EditorGUILayout.Vector3Field(label, As<Vector3>(value));
            if (type == typeof(Color)) return EditorGUILayout.ColorField(label, value is Color color ? color : Color.white);
            if (typeof(Object).IsAssignableFrom(type)) return EditorGUILayout.ObjectField(label, value as Object, type, false);

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(CultRecordRef<>)) return DrawRecordRef(label, type, value);
            var dictionary = DictionaryArguments(type);
            if (dictionary != null) return DrawDictionary(label, type, dictionary, value as IDictionary, member, path);
            var element = ListElement(type);
            if (element != null) return DrawList(label, type, element, value, member, path);
            if (type.IsAbstract || type.IsInterface) return DrawUnion(label, type, value, path);
            if (type.IsClass || type.IsValueType && !type.IsPrimitive) return DrawNested(label, type, value, path);
            return ErrorRow(label, "no drawer for " + type.FullName, value);
        }

        private void DrawMembers(object target, Type type, string path)
        {
            foreach (var member in MembersOf(type))
            {
                if (member.GetCustomAttribute<CultInspectorHiddenAttribute>() != null) continue;
                var current = Get(member, target);
                var readOnly = !Writable(member) || member.GetCustomAttribute<CultInspectorReadOnlyAttribute>() != null;
                using (new EditorGUI.DisabledScope(readOnly))
                {
                    EditorGUI.BeginChangeCheck();
                    var next = DrawValue(LabelOf(member), TypeOf(member), current, member, path + "." + member.Name);
                    if (EditorGUI.EndChangeCheck() && !readOnly) Set(member, target, next);
                }
            }
        }

        // Document members come from the registry's catalog; nested members are the MessagePack-keyed ones,
        // or the public fields of an unkeyed struct.
        private MemberInfo[] MembersOf(Type type)
        {
            if (_members.TryGetValue(type, out var cached)) return cached;
            var candidates = type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m is FieldInfo || m is PropertyInfo p && p.CanRead && p.GetIndexParameters().Length == 0)
                .Where(m => m.GetCustomAttribute<IgnoreMemberAttribute>() == null)
                .ToArray();

            IEnumerable<(MemberInfo Member, int Slot)> slotted;
            if (type.IsDefined(typeof(CultDocumentAttribute), false))
            {
                var slots = _cache.Registry.GetRequired(type).ToCatalogEntry().Members.ToDictionary(m => m.MemberName, m => m.Slot);
                slotted = candidates.Where(m => slots.ContainsKey(m.Name)).Select(m => (m, slots[m.Name]));
            }
            else if (candidates.Any(m => m.IsDefined(typeof(KeyAttribute), true)))
            {
                slotted = candidates.Where(m => m.IsDefined(typeof(KeyAttribute), true))
                    .Select(m => (m, m.GetCustomAttribute<KeyAttribute>().IntKey ?? int.MaxValue));
            }
            else
            {
                slotted = type.IsValueType
                    ? candidates.OfType<FieldInfo>().Select(f => ((MemberInfo)f, f.MetadataToken))
                    : Enumerable.Empty<(MemberInfo, int)>();
            }

            cached = slotted
                .OrderBy(s => s.Member.GetCustomAttribute<CultInspectorOrderAttribute>()?.Order ?? s.Slot)
                .ThenBy(s => s.Slot)
                .Select(s => s.Member)
                .ToArray();
            _members[type] = cached;
            return cached;
        }

        private static string DrawString(string label, string value, MemberInfo member)
        {
            var assetPath = member?.GetCustomAttribute<CultInspectorAssetPathAttribute>();
            if (assetPath != null)
            {
                var assetType = assetPath.AssetType != null && typeof(Object).IsAssignableFrom(assetPath.AssetType) ? assetPath.AssetType : typeof(Object);
                var asset = string.IsNullOrEmpty(value) ? null : AssetDatabase.LoadAssetAtPath(value, assetType);
                var next = EditorGUILayout.ObjectField(label, asset, assetType, false);
                return next == asset ? value : next == null ? string.Empty : AssetDatabase.GetAssetPath(next);
            }

            var textArea = member?.GetCustomAttribute<CultInspectorTextAreaAttribute>();
            if (textArea == null) return EditorGUILayout.TextField(label, value ?? string.Empty);
            EditorGUILayout.LabelField(label);
            return EditorGUILayout.TextArea(value ?? string.Empty,
                GUILayout.MinHeight(Mathf.Max(textArea.MinLines, 1) * EditorGUIUtility.singleLineHeight),
                GUILayout.MaxHeight(Mathf.Max(textArea.MaxLines, textArea.MinLines, 1) * EditorGUIUtility.singleLineHeight));
        }

        private object DrawRecordRef(string label, Type type, object value)
        {
            var target = type.GetGenericArguments()[0];
            var key = value == null ? string.Empty : ((CultRecordKey)type.GetProperty(nameof(CultRecordRef<object>.Key)).GetValue(value)).Value ?? string.Empty;
            var candidates = Records.Where(r => target.IsInstanceOfType(r.Document))
                .Select(r => (Record: r, Label: RecordLabel(r)))
                .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var index = Array.FindIndex(candidates, c => c.Record.Key.Value == key) + 1;
            var names = new string[candidates.Length + 1];
            names[0] = index == 0 && key.Length > 0 ? "Missing " + key : "None";
            for (var i = 0; i < candidates.Length; i++) names[i + 1] = candidates[i].Label;

            var next = key;
            using (new EditorGUILayout.HorizontalScope())
            {
                var picked = EditorGUILayout.Popup(label, index, names);
                var indent = EditorGUI.indentLevel;
                EditorGUI.indentLevel = 0;
                var typed = EditorGUILayout.DelayedTextField(key, GUILayout.Width(120));
                EditorGUI.indentLevel = indent;
                if (picked != index) next = picked == 0 ? string.Empty : candidates[picked - 1].Record.Key.Value;
                else if (typed != key) next = typed;
            }

            return next == key ? value : Activator.CreateInstance(type, new CultRecordKey(next));
        }

        private object DrawDictionary(string label, Type type, Type[] arguments, IDictionary dictionary, MemberInfo member, string path)
        {
            var entries = new List<DictionaryEntry>();
            if (dictionary != null)
            {
                var enumerator = dictionary.GetEnumerator();
                while (enumerator.MoveNext()) entries.Add(enumerator.Entry);
            }

            if (!Foldout(path, label + " (" + entries.Count + ")")) return dictionary;
            EditorGUI.indentLevel++;
            var changed = false;
            var remove = -1;
            for (var i = 0; i < entries.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        var key = DrawValue("Key", arguments[0], entries[i].Key, null, path + "{" + i + "}.key");
                        var item = DrawValue("Value", arguments[1], entries[i].Value, member, path + "{" + i + "}.value");
                        if (EditorGUI.EndChangeCheck())
                        {
                            var index = i;
                            if (key == null || entries.Where((_, j) => j != index).Any(e => Equals(e.Key, key))) key = entries[i].Key;
                            entries[i] = new DictionaryEntry(key, item);
                            changed = true;
                        }
                    }

                    if (GUILayout.Button("-", GUILayout.Width(20))) remove = i;
                }
            }

            if (remove >= 0)
            {
                entries.RemoveAt(remove);
                changed = true;
            }

            var fresh = CreateDefault(arguments[0]);
            using (new EditorGUI.DisabledScope(fresh == null || entries.Any(e => Equals(e.Key, fresh))))
            {
                if (GUILayout.Button("Add"))
                {
                    entries.Add(new DictionaryEntry(fresh, CreateDefault(arguments[1])));
                    changed = true;
                }
            }

            EditorGUI.indentLevel--;
            if (!changed) return dictionary;
            var result = dictionary ?? (IDictionary)Activator.CreateInstance(type.IsInterface || type.IsAbstract
                ? typeof(Dictionary<,>).MakeGenericType(arguments)
                : type);
            result.Clear();
            foreach (var entry in entries) result.Add(entry.Key, entry.Value);
            return result;
        }

        private object DrawList(string label, Type type, Type element, object value, MemberInfo member, string path)
        {
            var items = new List<object>();
            if (value is IEnumerable enumerable)
                foreach (var item in enumerable) items.Add(item);

            if (!Foldout(path, label + " [" + items.Count + "]")) return value;
            EditorGUI.indentLevel++;
            var changed = false;
            var remove = -1;
            for (var i = 0; i < items.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        var next = DrawValue("Element " + i, element, items[i], member, path + "[" + i + "]");
                        if (EditorGUI.EndChangeCheck())
                        {
                            items[i] = next;
                            changed = true;
                        }
                    }

                    if (GUILayout.Button("-", GUILayout.Width(20))) remove = i;
                }
            }

            if (remove >= 0)
            {
                items.RemoveAt(remove);
                changed = true;
            }

            if (element.IsAbstract || element.IsInterface)
            {
                var unions = UnionsOf(element);
                var picked = EditorGUILayout.Popup("Add", 0, unions.Select(u => u.Name).Prepend("...").ToArray());
                if (picked > 0 && unions[picked - 1].GetConstructor(Type.EmptyTypes) != null)
                {
                    items.Add(Activator.CreateInstance(unions[picked - 1]));
                    changed = true;
                }
            }
            else if (GUILayout.Button("Add"))
            {
                items.Add(CreateDefault(element));
                changed = true;
            }

            EditorGUI.indentLevel--;
            if (!changed) return value;
            if (type.IsArray)
            {
                var array = Array.CreateInstance(element, items.Count);
                for (var i = 0; i < items.Count; i++) array.SetValue(items[i], i);
                return array;
            }

            var list = value as IList ?? (IList)Activator.CreateInstance(type);
            list.Clear();
            foreach (var item in items) list.Add(item);
            return list;
        }

        private object DrawUnion(string label, Type type, object value, string path)
        {
            var unions = UnionsOf(type);
            if (unions.Length == 0) return ErrorRow(label, type.Name + " declares no [Union] subtypes", value);
            var index = value == null ? 0 : Array.IndexOf(unions, value.GetType()) + 1;
            var names = unions.Select(u => u.Name).Prepend(value == null ? "None" : value.GetType().Name + " (not a declared union)").ToArray();

            bool expanded;
            using (new EditorGUILayout.HorizontalScope())
            {
                expanded = Foldout(path, label, GUILayout.Width(EditorGUIUtility.labelWidth));
                var indent = EditorGUI.indentLevel;
                EditorGUI.indentLevel = 0;
                var picked = EditorGUILayout.Popup(index, names);
                EditorGUI.indentLevel = indent;
                if (picked != index)
                {
                    if (picked == 0) value = null;
                    else if (unions[picked - 1].GetConstructor(Type.EmptyTypes) != null) value = Activator.CreateInstance(unions[picked - 1]);
                    else Debug.LogError(unions[picked - 1].FullName + " has no parameterless constructor.");
                }
            }

            if (!expanded || value == null) return value;
            EditorGUI.indentLevel++;
            DrawMembers(value, value.GetType(), path);
            EditorGUI.indentLevel--;
            return value;
        }

        private object DrawNested(string label, Type type, object value, string path)
        {
            if (MembersOf(type).Length == 0) return ErrorRow(label, "no drawer for " + type.FullName, value);
            if (value == null)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(label, "null");
                    if (type.GetConstructor(Type.EmptyTypes) != null && GUILayout.Button("Create", GUILayout.Width(56)))
                        value = Activator.CreateInstance(type);
                }

                return value;
            }

            if (!Foldout(path, label)) return value;
            EditorGUI.indentLevel++;
            DrawMembers(value, type, path);
            EditorGUI.indentLevel--;
            return value;
        }

        private static double[] DrawDoubles(string label, params double[] values)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);
                var indent = EditorGUI.indentLevel;
                EditorGUI.indentLevel = 0;
                for (var i = 0; i < values.Length; i++) values[i] = EditorGUILayout.DoubleField(values[i]);
                EditorGUI.indentLevel = indent;
            }

            return values;
        }

        private static CM.bool2 DrawBool2(string label, CM.bool2 value)
        {
            bool x, y;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);
                var indent = EditorGUI.indentLevel;
                EditorGUI.indentLevel = 0;
                x = EditorGUILayout.ToggleLeft("X", value.x, GUILayout.Width(36));
                y = EditorGUILayout.ToggleLeft("Y", value.y, GUILayout.Width(36));
                EditorGUI.indentLevel = indent;
            }

            return new CM.bool2(x, y);
        }

        // Foldouts stay usable inside disabled scopes so read-only data can still be browsed.
        private bool Foldout(string path, string label, params GUILayoutOption[] options)
        {
            _foldouts.TryGetValue(path, out var expanded);
            var enabled = GUI.enabled;
            GUI.enabled = true;
            expanded = EditorGUI.Foldout(EditorGUILayout.GetControlRect(false, options), expanded, label, true);
            GUI.enabled = enabled;
            _foldouts[path] = expanded;
            return expanded;
        }

        private static object ErrorRow(string label, string message, object value)
        {
            EditorGUILayout.LabelField(label, message, ErrorStyle);
            return value;
        }

        private static Type[] UnionsOf(Type type) =>
            type.GetCustomAttributes<UnionAttribute>(false).OrderBy(u => u.Key).Select(u => u.SubType).ToArray();

        private static Type[] DictionaryArguments(Type type) =>
            type.GetInterfaces().Prepend(type)
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                ?.GetGenericArguments();

        private static Type ListElement(Type type)
        {
            if (type.IsArray) return type.GetElementType();
            return type.IsGenericType && !type.IsAbstract && !type.IsInterface && typeof(IList).IsAssignableFrom(type)
                ? type.GetGenericArguments()[0]
                : null;
        }

        private static object CreateDefault(Type type)
        {
            if (type == typeof(string)) return string.Empty;
            if (type.IsValueType) return Activator.CreateInstance(type);
            if (type.IsArray) return Array.CreateInstance(type.GetElementType(), 0);
            return type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) == null ? null : Activator.CreateInstance(type);
        }

        private static T As<T>(object value) => value is T typed ? typed : default;

        private static string LabelOf(MemberInfo member)
        {
            var label = member.GetCustomAttribute<CultInspectorLabelAttribute>();
            return label == null || string.IsNullOrWhiteSpace(label.Label) ? ObjectNames.NicifyVariableName(member.Name) : label.Label;
        }

        private static Type TypeOf(MemberInfo member) => member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;

        private static object Get(MemberInfo member, object target) => member is FieldInfo field ? field.GetValue(target) : ((PropertyInfo)member).GetValue(target);

        private static void Set(MemberInfo member, object target, object value)
        {
            if (member is FieldInfo field) field.SetValue(target, value);
            else ((PropertyInfo)member).SetValue(target, value);
        }

        private static bool Writable(MemberInfo member) =>
            member is FieldInfo field ? !field.IsInitOnly && !field.IsLiteral : ((PropertyInfo)member).GetSetMethod() != null;
    }
}
