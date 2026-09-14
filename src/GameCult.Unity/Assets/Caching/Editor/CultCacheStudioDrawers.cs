using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameCult.Caching;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameCult.Unity.Caching.Editor
{
    // An IMGUI drawer claimed through [CultInspectorDrawer]. A change returns the new value and sets GUI.changed, as IMGUI
    // controls do; only a frame that reports a change is saved. Mutating value in place without GUI.changed is not saved
    // reliably: it lingers in the edit copy until some later change saves it. A drawer that throws discards the whole
    // frame's edit copy, so nothing it or any other drawer did that frame is saved. type is the value's declared type.
    // member is the member the value belongs to, also for list elements and dictionary keys and values, and null for a
    // bare value. inspector.DrawDefault hands the value back to built-in drawing; inspector.DrawValue draws a sub-value
    // with claims.
    public interface ICultInspectorDrawer
    {
        object Draw(CultInspector inspector, string label, Type type, object value, MemberInfo member);
    }

    // The IMGUI lowering of CultInspectorModel. The model decides members, metadata, shapes, drawer claims and edit
    // rules; this class draws them and hands every change back through the model.
    public sealed class CultInspector
    {
        private static CultInspectorDrawerClaims _claims;
        private static readonly Dictionary<Type, ICultInspectorDrawer> DrawerInstances = new Dictionary<Type, ICultInspectorDrawer>();
        private static GUIStyle _errorStyle;

        private readonly Dictionary<string, bool> _foldouts = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _notices = new Dictionary<string, string>(StringComparer.Ordinal);
        private string _path = string.Empty;
        private bool _drawerFailed;

        internal CultInspector(CultInspectorModel model)
        {
            Model = model;
        }

        public CultInspectorModel Model { get; }

        public IReadOnlyList<CultStoredDocument> Records { get; internal set; } = Array.Empty<CultStoredDocument>();

        private static GUIStyle ErrorStyle => _errorStyle ??= new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(1f, .35f, .3f) } };

        private static CultInspectorDrawerClaims Claims
        {
            get
            {
                if (_claims != null) return _claims;
                _claims = new CultInspectorDrawerClaims(TypeCache.GetTypesWithAttribute<CultInspectorDrawerAttribute>(), typeof(ICultInspectorDrawer));
                foreach (var error in _claims.Errors) Debug.LogError(error);
                return _claims;
            }
        }

        // False when a drawer threw: the edit copy may hold part of its in-place mutation and must be discarded, not saved.
        internal bool DrawDocument(CultInspectorEdit edit)
        {
            _drawerFailed = false;
            DrawMembers(edit.Document, edit.Source.Descriptor.DocumentType, edit.Source.Key.Value);
            return !_drawerFailed;
        }

        // Draws a sub-value of the value being drawn, claims applied.
        public object DrawValue(string label, Type type, object value, MemberInfo member)
        {
            return DrawValue(label, type, value, member, _path + "/" + label);
        }

        // Built-in drawing for a value, ignoring drawer claims on it.
        public object DrawDefault(string label, Type type, object value, MemberInfo member)
        {
            var shape = Model.ShapeOf(type);
            var metadata = Model.MetadataOf(member);
            switch (shape.Kind)
            {
                case CultInspectorValueKind.String:
                    return DrawString(label, value as string, metadata);
                case CultInspectorValueKind.Integer:
                    return DrawInteger(label, type, value, metadata);
                case CultInspectorValueKind.Float:
                    if (type == typeof(double)) return EditorGUILayout.DoubleField(label, value is double d ? d : 0d);
                    var f = value is float single ? single : 0f;
                    return metadata.Range == null ? EditorGUILayout.FloatField(label, f) : EditorGUILayout.Slider(label, f, metadata.Range.Min, metadata.Range.Max);
                case CultInspectorValueKind.Bool:
                    return EditorGUILayout.Toggle(label, value is bool b && b);
                case CultInspectorValueKind.Enum:
                    var current = value as Enum ?? (Enum)Enum.ToObject(type, 0);
                    return type.IsDefined(typeof(FlagsAttribute), false) ? EditorGUILayout.EnumFlagsField(label, current) : EditorGUILayout.EnumPopup(label, current);
                case CultInspectorValueKind.RecordRef:
                    return DrawRecordRef(label, type, value);
                case CultInspectorValueKind.List:
                    return DrawList(label, shape, value, member);
                case CultInspectorValueKind.Dictionary:
                    return DrawDictionary(label, shape, value as IDictionary, member);
                case CultInspectorValueKind.Union:
                    return DrawUnion(label, shape, value);
                case CultInspectorValueKind.Nested:
                    return DrawNested(label, type, value);
                default:
                    return typeof(Object).IsAssignableFrom(type)
                        ? EditorGUILayout.ObjectField(label, value as Object, type, false)
                        : ErrorRow(label, shape.Reason, value);
            }
        }

        private object DrawValue(string label, Type type, object value, MemberInfo member, string path)
        {
            var outer = _path;
            _path = path;
            try
            {
                var claim = Claims.Resolve(type, member);
                if (claim.Conflict != null) return ErrorRow(label, "no drawer: " + claim.Conflict, value);
                if (claim.Drawer == null) return DrawDefault(label, type, value, member);
                var changed = GUI.changed;
                try
                {
                    return Drawer(claim.Drawer).Draw(this, label, type, value, member);
                }
                catch (ExitGUIException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    GUI.changed = changed;
                    _drawerFailed = true;
                    return ErrorRow(label, claim.Drawer.Name + " failed: " + exception.Message + " Nothing edited this frame was saved.", value);
                }
            }
            finally
            {
                _path = outer;
            }
        }

        private static ICultInspectorDrawer Drawer(Type type)
        {
            if (!DrawerInstances.TryGetValue(type, out var drawer))
                DrawerInstances[type] = drawer = (ICultInspectorDrawer)Activator.CreateInstance(type);
            return drawer;
        }

        private void DrawMembers(object target, Type type, string path)
        {
            foreach (var member in Model.MembersOf(type))
            {
                if (member.Metadata.Hidden) continue;
                using (new EditorGUI.DisabledScope(member.IsReadOnly))
                {
                    EditorGUI.BeginChangeCheck();
                    var next = DrawValue(LabelOf(member), member.ValueType, member.GetValue(target), member.Member, path + "." + member.Name);
                    if (EditorGUI.EndChangeCheck() && !member.IsReadOnly) member.SetValue(target, next);
                }
            }
        }

        private static string DrawString(string label, string value, CultInspectorMetadata metadata)
        {
            if (metadata.AssetPath != null)
            {
                var assetType = metadata.AssetPath.AssetType != null && typeof(Object).IsAssignableFrom(metadata.AssetPath.AssetType) ? metadata.AssetPath.AssetType : typeof(Object);
                var asset = string.IsNullOrEmpty(value) ? null : AssetDatabase.LoadAssetAtPath(value, assetType);
                var next = EditorGUILayout.ObjectField(label, asset, assetType, false);
                return next == asset ? value : next == null ? string.Empty : AssetDatabase.GetAssetPath(next);
            }

            var textArea = metadata.TextArea;
            if (textArea == null) return EditorGUILayout.TextField(label, value ?? string.Empty);
            EditorGUILayout.LabelField(label);
            return EditorGUILayout.TextArea(value ?? string.Empty,
                GUILayout.MinHeight(Mathf.Max(textArea.MinLines, 1) * EditorGUIUtility.singleLineHeight),
                GUILayout.MaxHeight(Mathf.Max(textArea.MaxLines, textArea.MinLines, 1) * EditorGUIUtility.singleLineHeight));
        }

        private object DrawInteger(string label, Type type, object value, CultInspectorMetadata metadata)
        {
            var current = value == null ? 0L : Convert.ToInt64(value);
            if (type == typeof(int) && metadata.Range != null)
                return EditorGUILayout.IntSlider(label, (int)current, Mathf.RoundToInt(metadata.Range.Min), Mathf.RoundToInt(metadata.Range.Max));
            var next = EditorGUILayout.LongField(label, current);
            return next == current && value != null ? value : Model.NarrowInteger(type, next);
        }

        private object DrawRecordRef(string label, Type type, object value)
        {
            var key = CultInspectorModel.RecordKey(value);
            var candidates = Model.RecordCandidates(type, Records);
            var index = 0;
            var names = new string[candidates.Count + 1];
            for (var i = 0; i < candidates.Count; i++)
            {
                names[i + 1] = CultInspectorModel.RecordLabel(candidates[i]);
                if (candidates[i].Key.Value == key) index = i + 1;
            }

            names[0] = index == 0 && key.Length > 0 ? "Missing " + key : "None";
            var next = key;
            using (new EditorGUILayout.HorizontalScope())
            {
                var picked = EditorGUILayout.Popup(label, index, names);
                var indent = EditorGUI.indentLevel;
                EditorGUI.indentLevel = 0;
                var typed = EditorGUILayout.DelayedTextField(key, GUILayout.Width(120));
                EditorGUI.indentLevel = indent;
                if (picked != index) next = picked == 0 ? string.Empty : candidates[picked - 1].Key.Value;
                else if (typed != key) next = typed;
            }

            return next == key ? value : Model.CreateRecordRef(type, next);
        }

        private object DrawDictionary(string label, CultInspectorShape shape, IDictionary dictionary, MemberInfo member)
        {
            var path = _path;
            var entries = new List<KeyValuePair<object, object>>();
            if (dictionary != null)
            {
                var enumerator = dictionary.GetEnumerator();
                while (enumerator.MoveNext()) entries.Add(new KeyValuePair<object, object>(enumerator.Key, enumerator.Value));
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
                        var key = DrawValue("Key", shape.KeyType, entries[i].Key, member, path + "{" + i + "}.key");
                        var item = DrawValue("Value", shape.ValueType, entries[i].Value, member, path + "{" + i + "}.value");
                        if (EditorGUI.EndChangeCheck())
                        {
                            key = Model.ReplaceKey(shape.Type, entries.Select(e => e.Key).ToArray(), i, key, out var notice);
                            Notice(path, notice);
                            entries[i] = new KeyValuePair<object, object>(key, item);
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

            if (Add(shape.ValueType, path, out var created))
            {
                var fresh = Model.FreshKey(shape.Type, entries.Select(e => e.Key).ToArray(), Records, out var notice);
                Notice(path, notice);
                if (fresh != null)
                {
                    entries.Add(new KeyValuePair<object, object>(fresh, created));
                    changed = true;
                }
            }

            DrawNotice(path);
            EditorGUI.indentLevel--;
            return changed ? Model.BuildDictionary(shape.Type, entries) : dictionary;
        }

        private object DrawList(string label, CultInspectorShape shape, object value, MemberInfo member)
        {
            var path = _path;
            var element = shape.ElementType;
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

            if (Add(element, path, out var created))
            {
                items.Add(created);
                changed = true;
            }

            DrawNotice(path);
            EditorGUI.indentLevel--;
            return changed ? Model.BuildList(shape.Type, items) : value;
        }

        private object DrawUnion(string label, CultInspectorShape shape, object value)
        {
            var path = _path;
            var choices = shape.UnionChoices;
            var index = value == null ? 0 : IndexOf(choices, value.GetType()) + 1;
            var names = choices.Select(u => u.Name).Prepend(value == null || index > 0 ? "None" : value.GetType().Name + " (not a declared union)").ToArray();

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
                    string notice = null;
                    value = picked == 0 ? null : Model.CreateElement(shape.Type, choices[picked - 1], out notice) ?? value;
                    Notice(path, notice);
                }
            }

            DrawNotice(path);
            if (!expanded || value == null) return value;
            EditorGUI.indentLevel++;
            DrawMembers(value, value.GetType(), path);
            EditorGUI.indentLevel--;
            return value;
        }

        private object DrawNested(string label, Type type, object value)
        {
            var path = _path;
            if (value == null)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(label, "null");
                    var created = Model.CreateDefault(type);
                    if (created != null && GUILayout.Button("Create", GUILayout.Width(56))) value = created;
                }

                return value;
            }

            if (!Foldout(path, label)) return value;
            EditorGUI.indentLevel++;
            DrawMembers(value, type, path);
            EditorGUI.indentLevel--;
            return value;
        }

        // Foldouts stay usable inside disabled scopes so read-only data can still be browsed. Expanding is view state:
        // it never counts as a change, so it never upserts.
        private bool Foldout(string path, string label, params GUILayoutOption[] options)
        {
            _foldouts.TryGetValue(path, out var expanded);
            var enabled = GUI.enabled;
            var changed = GUI.changed;
            GUI.enabled = true;
            expanded = EditorGUI.Foldout(EditorGUILayout.GetControlRect(false, options), expanded, label, true);
            GUI.enabled = enabled;
            GUI.changed = changed;
            _foldouts[path] = expanded;
            return expanded;
        }

        // The add control under a list or dictionary: a button when the model offers the element type itself, a popup of
        // a union's declared subtypes otherwise. True with the model's new value once one is picked and made.
        private bool Add(Type elementType, string path, out object created)
        {
            created = null;
            var choices = Model.ElementChoices(elementType);
            Type choice;
            if (choices.Count == 1 && choices[0] == elementType)
            {
                choice = GUILayout.Button("Add") ? elementType : null;
            }
            else
            {
                var picked = EditorGUILayout.Popup("Add", 0, choices.Select(c => c.Name).Prepend("...").ToArray());
                choice = picked > 0 ? choices[picked - 1] : null;
            }

            if (choice == null) return false;
            created = Model.CreateElement(elementType, choice, out var notice);
            Notice(path, notice);
            return notice == null;
        }

        // The model's notice for the control at path, kept until that control's next answer.
        private void Notice(string path, string notice)
        {
            if (notice == null) _notices.Remove(path);
            else _notices[path] = notice;
        }

        private void DrawNotice(string path)
        {
            if (_notices.TryGetValue(path, out var notice)) EditorGUILayout.HelpBox(notice, MessageType.Warning);
        }

        private static object ErrorRow(string label, string message, object value)
        {
            EditorGUILayout.LabelField(label, message, ErrorStyle);
            return value;
        }

        private static int IndexOf(IReadOnlyList<Type> types, Type type)
        {
            for (var i = 0; i < types.Count; i++)
                if (types[i] == type) return i;
            return -1;
        }

        private static string LabelOf(CultInspectorMember member) => member.Metadata.Label ?? ObjectNames.NicifyVariableName(member.Name);
    }
}
