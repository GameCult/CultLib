using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GameCult.Unity.Caching;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameCult.Unity.Caching.Editor
{
    /// <summary>
    /// Unity editor window for inspecting and editing CultCache .cc stores.
    /// </summary>
    public sealed class CultCacheStudioWindow : EditorWindow
    {
        private const string LastPathKey = "GameCult.CultCacheStudio.LastPath";

        private readonly Dictionary<string, bool> _foldouts = new Dictionary<string, bool>(StringComparer.Ordinal);
        private object _cache;
        private string _path = string.Empty;
        private string _search = string.Empty;
        private string _selectedKey = string.Empty;
        private Type _selectedType;
        private Vector2 _typeScroll;
        private Vector2 _recordScroll;
        private Vector2 _inspectorScroll;
        private string _status = "Open a .cc file to begin.";
        private MessageType _statusType = MessageType.Info;

        [MenuItem("GameCult/CultCache Studio")]
        public static void Open()
        {
            GetWindow<CultCacheStudioWindow>("CultCache Studio");
        }

        private void OnEnable()
        {
            _path = EditorPrefs.GetString(LastPathKey, string.Empty);
        }

        private void OnGUI()
        {
            DrawToolbar();
            EditorGUILayout.Space(6);

            if (!CultCacheBridge.IsAvailable)
            {
                EditorGUILayout.HelpBox(
                    "CultCache Studio needs GameCult.Caching and GameCult.Caching.MessagePack loaded in this Unity project.",
                    MessageType.Warning);
            }

            if (!string.IsNullOrWhiteSpace(_status))
            {
                EditorGUILayout.HelpBox(_status, _statusType);
            }

            if (_cache == null)
            {
                DrawClosedState();
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawTypeColumn();
                DrawRecordColumn();
                DrawInspectorColumn();
            }
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Path", GUILayout.Width(32));
                _path = GUILayout.TextField(_path, EditorStyles.toolbarTextField, GUILayout.MinWidth(180));

                using (new EditorGUI.DisabledScope(!CultCacheBridge.IsAvailable))
                {
                    if (GUILayout.Button("Open", EditorStyles.toolbarButton, GUILayout.Width(52)))
                    {
                        BrowseAndOpen();
                    }

                    if (GUILayout.Button("New", EditorStyles.toolbarButton, GUILayout.Width(44)))
                    {
                        BrowseAndCreate();
                    }
                }

                using (new EditorGUI.DisabledScope(_cache == null))
                {
                    if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(58)))
                    {
                        ReloadCurrent();
                    }

                    if (GUILayout.Button("Save", EditorStyles.toolbarButton, GUILayout.Width(48)))
                    {
                        SaveCurrent();
                    }
                }

                GUILayout.FlexibleSpace();
                if (_cache != null)
                {
                    GUILayout.Label(CultCacheBridge.IsDirty(_cache) ? "Dirty" : "Saved", EditorStyles.miniLabel, GUILayout.Width(42));
                }
            }
        }

        private void DrawClosedState()
        {
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(420)))
                {
                    EditorGUILayout.LabelField("CultCache Studio", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("Open or create a .cc file to inspect registered CultCache documents.");
                    EditorGUILayout.Space(8);
                    using (new EditorGUI.DisabledScope(!CultCacheBridge.IsAvailable))
                    {
                        if (GUILayout.Button("Open .cc File", GUILayout.Height(32)))
                        {
                            BrowseAndOpen();
                        }

                        if (GUILayout.Button("Create .cc File", GUILayout.Height(28)))
                        {
                            BrowseAndCreate();
                        }
                    }
                }

                GUILayout.FlexibleSpace();
            }

            GUILayout.FlexibleSpace();
        }

        private void DrawTypeColumn()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(250), GUILayout.ExpandHeight(true)))
            {
                EditorGUILayout.LabelField("Document Types", EditorStyles.boldLabel);
                _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);
                _typeScroll = EditorGUILayout.BeginScrollView(_typeScroll, GUI.skin.box);

                foreach (var descriptor in FilteredDescriptors())
                {
                    var count = RecordsFor(descriptor.DocumentType).Count();
                    var label = descriptor.SchemaName + " (" + count.ToString(CultureInfo.InvariantCulture) + ")";
                    var style = _selectedType == descriptor.DocumentType ? EditorStyles.toolbarButton : EditorStyles.miniButton;
                    if (GUILayout.Button(label, style))
                    {
                        _selectedType = descriptor.DocumentType;
                        _selectedKey = string.Empty;
                    }
                }

                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawRecordColumn()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(280), GUILayout.ExpandHeight(true)))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Records", EditorStyles.boldLabel);
                    using (new EditorGUI.DisabledScope(_selectedType == null || _selectedType.GetConstructor(Type.EmptyTypes) == null))
                    {
                        if (GUILayout.Button("Add", GUILayout.Width(56)))
                        {
                            AddSelectedTypeRecord();
                        }
                    }
                }

                _recordScroll = EditorGUILayout.BeginScrollView(_recordScroll, GUI.skin.box);
                if (_selectedType == null)
                {
                    EditorGUILayout.LabelField("Select a document type.");
                }
                else
                {
                    foreach (var record in RecordsFor(_selectedType))
                    {
                        var style = string.Equals(_selectedKey, record.Key, StringComparison.Ordinal)
                            ? EditorStyles.toolbarButton
                            : EditorStyles.miniButton;
                        if (GUILayout.Button(GetRecordLabel(record), style))
                        {
                            _selectedKey = record.Key;
                        }
                    }
                }

                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawInspectorColumn()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)))
            {
                var record = SelectedRecord();
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Inspector", EditorStyles.boldLabel);
                    using (new EditorGUI.DisabledScope(record == null))
                    {
                        if (GUILayout.Button("Delete", GUILayout.Width(64)))
                        {
                            DeleteSelectedRecord();
                            return;
                        }
                    }
                }

                _inspectorScroll = EditorGUILayout.BeginScrollView(_inspectorScroll, GUI.skin.box);
                if (record == null)
                {
                    EditorGUILayout.LabelField("Select a record.");
                    EditorGUILayout.EndScrollView();
                    return;
                }

                EditorGUILayout.SelectableLabel(record.Descriptor.SchemaName, EditorStyles.boldLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.SelectableLabel("Key: " + record.Key, EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.SelectableLabel("Stored: " + record.StoredAt, EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.Space(8);

                EditorGUI.BeginChangeCheck();
                DrawDocument(record.Document, record.Descriptor.DocumentType, record.Descriptor.SchemaName);
                if (EditorGUI.EndChangeCheck())
                {
                    UpsertRecord(record);
                }

                EditorGUILayout.EndScrollView();
            }
        }

        private IEnumerable<DescriptorInfo> FilteredDescriptors()
        {
            var descriptors = CultCacheBridge.GetDescriptors(_cache);
            if (string.IsNullOrWhiteSpace(_search))
            {
                return descriptors;
            }

            return descriptors.Where(descriptor =>
                descriptor.SchemaName.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                descriptor.DocumentType.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private IEnumerable<RecordInfo> RecordsFor(Type type)
        {
            return CultCacheBridge.GetStoredDocuments(_cache)
                .Where(record => type.IsAssignableFrom(record.Descriptor.DocumentType))
                .OrderBy(GetRecordLabel, StringComparer.Ordinal);
        }

        private RecordInfo SelectedRecord()
        {
            if (_cache == null || string.IsNullOrEmpty(_selectedKey))
            {
                return null;
            }

            return CultCacheBridge.GetStoredDocuments(_cache)
                .FirstOrDefault(record => string.Equals(record.Key, _selectedKey, StringComparison.Ordinal));
        }

        private string GetRecordLabel(RecordInfo record)
        {
            var namedMember = record.Descriptor.Members.FirstOrDefault(member => member.IsName);
            if (namedMember != null)
            {
                var member = FindInspectableMember(record.Descriptor.DocumentType, namedMember.MemberName);
                if (member != null)
                {
                    var value = member.GetValue(record.Document);
                    if (value != null && !string.IsNullOrWhiteSpace(value.ToString()))
                    {
                        return value + " [" + record.Key + "]";
                    }
                }
            }

            return record.Key;
        }

        private void DrawDocument(object target, Type type, string path)
        {
            foreach (var member in GetInspectableMembers(type))
            {
                if (member.GetCustomAttribute<CultInspectorHiddenAttribute>() != null)
                {
                    continue;
                }

                var current = member.GetValue(target);
                var readOnly = member.GetCustomAttribute<CultInspectorReadOnlyAttribute>() != null || member.IsReadOnly;
                using (new EditorGUI.DisabledScope(readOnly))
                {
                    var next = DrawValue(GetLabel(member), member.MemberType, current, member, path + "." + member.Name);
                    if (!readOnly && !Equals(current, next))
                    {
                        member.SetValue(target, next);
                    }
                }
            }
        }

        private object DrawValue(string label, Type type, object value, InspectableMember member, string path)
        {
            if (type == typeof(string)) return DrawString(label, value as string, member);
            if (type == typeof(int))
            {
                var range = member.GetCustomAttribute<CultInspectorRangeAttribute>();
                return range == null
                    ? EditorGUILayout.IntField(label, value == null ? 0 : (int)value)
                    : EditorGUILayout.IntSlider(label, value == null ? 0 : (int)value, Mathf.RoundToInt(range.Min), Mathf.RoundToInt(range.Max));
            }

            if (type == typeof(uint)) return (uint)Math.Max(0L, EditorGUILayout.LongField(label, value == null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture)));
            if (type == typeof(long)) return EditorGUILayout.LongField(label, value == null ? 0L : (long)value);
            if (type == typeof(float))
            {
                var range = member.GetCustomAttribute<CultInspectorRangeAttribute>();
                return range == null
                    ? EditorGUILayout.FloatField(label, value == null ? 0f : (float)value)
                    : EditorGUILayout.Slider(label, value == null ? 0f : (float)value, range.Min, range.Max);
            }

            if (type == typeof(double)) return EditorGUILayout.DoubleField(label, value == null ? 0d : (double)value);
            if (type == typeof(bool)) return EditorGUILayout.Toggle(label, value != null && (bool)value);
            if (type.IsEnum) return EditorGUILayout.EnumPopup(label, value == null ? (Enum)Enum.GetValues(type).GetValue(0) : (Enum)value);
            if (type == typeof(Vector2)) return EditorGUILayout.Vector2Field(label, value == null ? Vector2.zero : (Vector2)value);
            if (type == typeof(Vector3)) return EditorGUILayout.Vector3Field(label, value == null ? Vector3.zero : (Vector3)value);
            if (type == typeof(Color)) return EditorGUILayout.ColorField(label, value == null ? Color.white : (Color)value);
            if (typeof(Object).IsAssignableFrom(type)) return EditorGUILayout.ObjectField(label, value as Object, type, false);
            if (CultCacheBridge.IsCultRecordRef(type)) return CultCacheBridge.DrawRecordRef(label, type, value);
            if (type.IsArray || typeof(IList).IsAssignableFrom(type)) return DrawList(label, type, value, member, path);
            if (type.IsClass || IsMutableStruct(type)) return DrawNestedObject(label, type, value, path);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField(label, value == null ? string.Empty : value.ToString());
            }

            return value;
        }

        private string DrawString(string label, string value, InspectableMember member)
        {
            var assetPath = member.GetCustomAttribute<CultInspectorAssetPathAttribute>();
            if (assetPath != null)
            {
                var assetType = assetPath.AssetType == null || !typeof(Object).IsAssignableFrom(assetPath.AssetType)
                    ? typeof(Object)
                    : assetPath.AssetType;
                var asset = string.IsNullOrEmpty(value) ? null : AssetDatabase.LoadAssetAtPath(value, assetType);
                var nextAsset = EditorGUILayout.ObjectField(label, asset, assetType, false);
                return nextAsset == null ? string.Empty : AssetDatabase.GetAssetPath(nextAsset);
            }

            var textArea = member.GetCustomAttribute<CultInspectorTextAreaAttribute>();
            if (textArea != null)
            {
                EditorGUILayout.LabelField(label);
                return EditorGUILayout.TextArea(value ?? string.Empty, GUILayout.MinHeight(Mathf.Max(textArea.MinLines, 1) * EditorGUIUtility.singleLineHeight));
            }

            return EditorGUILayout.TextField(label, value ?? string.Empty);
        }

        private object DrawList(string label, Type type, object value, InspectableMember member, string path)
        {
            if (!Foldout(path, label)) return value;

            var elementType = GetListElementType(type);
            var list = ToMutableList(elementType, value);
            EditorGUI.indentLevel++;
            var size = Mathf.Max(0, EditorGUILayout.IntField("Size", list.Count));
            while (list.Count < size) list.Add(CreateDefaultValue(elementType));
            while (list.Count > size) list.RemoveAt(list.Count - 1);

            for (var index = 0; index < list.Count; index++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    list[index] = DrawValue("Element " + index.ToString(CultureInfo.InvariantCulture), elementType, list[index], member, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                    if (GUILayout.Button("-", GUILayout.Width(24)))
                    {
                        list.RemoveAt(index);
                        index--;
                    }
                }
            }

            if (GUILayout.Button("Add " + elementType.Name)) list.Add(CreateDefaultValue(elementType));
            EditorGUI.indentLevel--;
            return FromMutableList(type, elementType, list);
        }

        private object DrawNestedObject(string label, Type type, object value, string path)
        {
            var expanded = Foldout(path, label);
            if (value == null && type.GetConstructor(Type.EmptyTypes) != null) value = Activator.CreateInstance(type);
            if (!expanded || value == null) return value;
            EditorGUI.indentLevel++;
            DrawDocument(value, type, path);
            EditorGUI.indentLevel--;
            return value;
        }

        private bool Foldout(string key, string label)
        {
            bool expanded;
            _foldouts.TryGetValue(key, out expanded);
            expanded = EditorGUILayout.Foldout(expanded, label, true);
            _foldouts[key] = expanded;
            return expanded;
        }

        private IEnumerable<InspectableMember> GetInspectableMembers(Type type)
        {
            var descriptor = CultCacheBridge.GetDescriptor(_cache, type);
            var byName = descriptor.Members.ToDictionary(member => member.MemberName, StringComparer.Ordinal);
            return type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(InspectableMember.TryCreate)
                .Where(member => member != null && byName.ContainsKey(member.Name))
                .OrderBy(member => member.GetCustomAttribute<CultInspectorOrderAttribute>() == null
                    ? byName[member.Name].Slot
                    : member.GetCustomAttribute<CultInspectorOrderAttribute>().Order)
                .ThenBy(member => byName[member.Name].Slot)
                .ThenBy(member => member.Name, StringComparer.Ordinal);
        }

        private static InspectableMember FindInspectableMember(Type type, string name)
        {
            return type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(InspectableMember.TryCreate)
                .FirstOrDefault(member => member != null && string.Equals(member.Name, name, StringComparison.Ordinal));
        }

        private static string GetLabel(InspectableMember member)
        {
            var label = member.GetCustomAttribute<CultInspectorLabelAttribute>();
            return label == null || string.IsNullOrWhiteSpace(label.Label)
                ? ObjectNames.NicifyVariableName(member.Name)
                : label.Label;
        }

        private static Type GetListElementType(Type type)
        {
            if (type.IsArray) return type.GetElementType();
            return type.IsGenericType ? type.GetGenericArguments()[0] : typeof(object);
        }

        private static IList ToMutableList(Type elementType, object value)
        {
            var result = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType));
            var source = value as IEnumerable;
            if (source != null)
            {
                foreach (var item in source) result.Add(item);
            }

            return result;
        }

        private static object FromMutableList(Type listType, Type elementType, IList list)
        {
            if (listType.IsArray)
            {
                var array = Array.CreateInstance(elementType, list.Count);
                for (var index = 0; index < list.Count; index++) array.SetValue(list[index], index);
                return array;
            }

            if (listType.IsAssignableFrom(list.GetType())) return list;
            var target = listType.GetConstructor(Type.EmptyTypes) == null ? null : Activator.CreateInstance(listType) as IList;
            if (target == null) return list;
            foreach (var item in list) target.Add(item);
            return target;
        }

        private static object CreateDefaultValue(Type type)
        {
            if (type == typeof(string)) return string.Empty;
            if (type.IsValueType) return Activator.CreateInstance(type);
            return type.GetConstructor(Type.EmptyTypes) == null ? null : Activator.CreateInstance(type);
        }

        private static bool IsMutableStruct(Type type)
        {
            return type.IsValueType && !type.IsPrimitive && !type.IsEnum;
        }

        private void BrowseAndOpen()
        {
            var start = string.IsNullOrEmpty(_path) ? Application.dataPath : Path.GetDirectoryName(_path);
            var selected = EditorUtility.OpenFilePanel("Open CultCache", start, "cc");
            if (!string.IsNullOrEmpty(selected))
            {
                _path = selected;
                OpenPath(true);
            }
        }

        private void BrowseAndCreate()
        {
            var start = string.IsNullOrEmpty(_path) ? Application.dataPath : Path.GetDirectoryName(_path);
            var selected = EditorUtility.SaveFilePanel("Create CultCache", start, "GameData", "cc");
            if (!string.IsNullOrEmpty(selected))
            {
                _path = selected;
                OpenPath(false);
                SaveCurrent();
            }
        }

        private void OpenPath(bool pullOnOpen)
        {
            try
            {
                _cache = CultCacheBridge.Open(_path, pullOnOpen);
                _selectedType = CultCacheBridge.GetDescriptors(_cache).FirstOrDefault()?.DocumentType;
                _selectedKey = string.Empty;
                EditorPrefs.SetString(LastPathKey, _path);
                SetStatus("Opened " + _path, MessageType.Info);
            }
            catch (Exception ex)
            {
                _cache = null;
                SetStatus("Failed to open CultCache: " + ex.GetBaseException().Message, MessageType.Error);
            }
        }

        private void ReloadCurrent()
        {
            if (string.IsNullOrWhiteSpace(_path))
            {
                SetStatus("No .cc path selected.", MessageType.Warning);
                return;
            }

            OpenPath(true);
        }

        private void SaveCurrent()
        {
            try
            {
                CultCacheBridge.Flush(_cache);
                SetStatus("Saved " + _path, MessageType.Info);
            }
            catch (Exception ex)
            {
                SetStatus("Failed to save CultCache: " + ex.GetBaseException().Message, MessageType.Error);
            }
        }

        private void AddSelectedTypeRecord()
        {
            try
            {
                var document = Activator.CreateInstance(_selectedType);
                _selectedKey = CultCacheBridge.Upsert(_cache, _selectedType, document, null);
                SetStatus("Added " + _selectedType.Name + ".", MessageType.Info);
            }
            catch (Exception ex)
            {
                SetStatus("Failed to add record: " + ex.GetBaseException().Message, MessageType.Error);
            }
        }

        private void DeleteSelectedRecord()
        {
            if (string.IsNullOrEmpty(_selectedKey)) return;
            if (!EditorUtility.DisplayDialog("Delete CultCache Record", "Delete record " + _selectedKey + "?", "Delete", "Cancel")) return;
            if (CultCacheBridge.Remove(_cache, _selectedKey))
            {
                _selectedKey = string.Empty;
                SetStatus("Deleted record.", MessageType.Info);
            }
        }

        private void UpsertRecord(RecordInfo record)
        {
            try
            {
                CultCacheBridge.Upsert(_cache, record.Descriptor.DocumentType, record.Document, record.Key);
                SetStatus("Edited " + record.Descriptor.SchemaName + ". Save to persist.", MessageType.Info);
            }
            catch (Exception ex)
            {
                SetStatus("Failed to apply edit: " + ex.GetBaseException().Message, MessageType.Error);
            }
        }

        private void SetStatus(string message, MessageType type)
        {
            _status = message;
            _statusType = type;
            Repaint();
        }

        private sealed class InspectableMember
        {
            private readonly FieldInfo _field;
            private readonly PropertyInfo _property;

            private InspectableMember(FieldInfo field)
            {
                _field = field;
                Name = field.Name;
                MemberType = field.FieldType;
                IsReadOnly = field.IsInitOnly || field.IsLiteral;
            }

            private InspectableMember(PropertyInfo property)
            {
                _property = property;
                Name = property.Name;
                MemberType = property.PropertyType;
                IsReadOnly = property.GetSetMethod(true) == null;
            }

            public string Name { get; }
            public Type MemberType { get; }
            public bool IsReadOnly { get; }

            public static InspectableMember TryCreate(MemberInfo member)
            {
                var field = member as FieldInfo;
                if (field != null && !field.IsStatic) return new InspectableMember(field);
                var property = member as PropertyInfo;
                return property != null && property.GetIndexParameters().Length == 0 && property.GetGetMethod(true) != null
                    ? new InspectableMember(property)
                    : null;
            }

            public T GetCustomAttribute<T>() where T : Attribute
            {
                return _field != null ? _field.GetCustomAttribute<T>(true) : _property.GetCustomAttribute<T>(true);
            }

            public object GetValue(object target)
            {
                return _field != null ? _field.GetValue(target) : _property.GetValue(target, null);
            }

            public void SetValue(object target, object value)
            {
                if (_field != null) _field.SetValue(target, value);
                else _property.SetValue(target, value, null);
            }
        }

        private static class CultCacheBridge
        {
            private static readonly Type CacheType = FindType("GameCult.Caching.CultCache");
            private static readonly Type OpenOptionsType = FindType("GameCult.Caching.MessagePack.CultCacheOpenOptions");
            private static readonly Type MessagePackType = FindType("GameCult.Caching.MessagePack.CultCacheMessagePack");
            private static readonly Type RecordKeyType = FindType("GameCult.Caching.CultRecordKey");

            public static bool IsAvailable => CacheType != null && OpenOptionsType != null && MessagePackType != null && RecordKeyType != null;

            public static object Open(string path, bool pullOnOpen)
            {
                EnsureAvailable();
                var options = Activator.CreateInstance(OpenOptionsType);
                OpenOptionsType.GetProperty("PullOnOpen").SetValue(options, pullOnOpen, null);
                var method = MessagePackType.GetMethod("OpenAsync", new[] { typeof(string), OpenOptionsType });
                var task = (Task)method.Invoke(null, new[] { path, options });
                task.GetAwaiter().GetResult();
                return task.GetType().GetProperty("Result").GetValue(task, null);
            }

            public static bool IsDirty(object cache)
            {
                return cache != null && (bool)CacheType.GetProperty("IsDirty").GetValue(cache, null);
            }

            public static IEnumerable<DescriptorInfo> GetDescriptors(object cache)
            {
                if (cache == null) return Enumerable.Empty<DescriptorInfo>();
                var registry = CacheType.GetProperty("Registry").GetValue(cache, null);
                var descriptors = (IEnumerable)registry.GetType().GetProperty("AllDescriptors").GetValue(registry, null);
                return descriptors.Cast<object>().Select(DescriptorInfo.From).ToArray();
            }

            public static DescriptorInfo GetDescriptor(object cache, Type documentType)
            {
                var registry = CacheType.GetProperty("Registry").GetValue(cache, null);
                var method = registry.GetType().GetMethod("GetRequired", new[] { typeof(Type) });
                return DescriptorInfo.From(method.Invoke(registry, new object[] { documentType }));
            }

            public static IEnumerable<RecordInfo> GetStoredDocuments(object cache)
            {
                if (cache == null) return Enumerable.Empty<RecordInfo>();
                var records = (IEnumerable)CacheType.GetProperty("AllStoredDocuments").GetValue(cache, null);
                return records.Cast<object>().Select(RecordInfo.From).ToArray();
            }

            public static string Upsert(object cache, Type documentType, object document, string key)
            {
                var method = CacheType.GetMethod("UpsertAsync", new[] { typeof(Type), typeof(object), typeof(Nullable<>).MakeGenericType(RecordKeyType) });
                var keyObject = string.IsNullOrEmpty(key) ? null : CreateRecordKey(key);
                var task = (Task)method.Invoke(cache, new[] { documentType, document, keyObject });
                task.GetAwaiter().GetResult();
                var result = task.GetType().GetProperty("Result").GetValue(task, null);
                return GetKeyValue(result);
            }

            public static bool Remove(object cache, string key)
            {
                var method = CacheType.GetMethod("Remove", new[] { RecordKeyType });
                return (bool)method.Invoke(cache, new[] { CreateRecordKey(key) });
            }

            public static void Flush(object cache)
            {
                var method = CacheType.GetMethod("FlushAsync", new[] { typeof(bool) });
                var task = (Task)method.Invoke(cache, new object[] { false });
                task.GetAwaiter().GetResult();
            }

            public static bool IsCultRecordRef(Type type)
            {
                return type.IsGenericType && type.GetGenericTypeDefinition().FullName == "GameCult.Caching.CultRecordRef`1";
            }

            public static object DrawRecordRef(string label, Type type, object value)
            {
                var keyProperty = type.GetProperty("Key");
                var currentKey = keyProperty == null || value == null ? string.Empty : GetKeyValue(keyProperty.GetValue(value, null));
                var nextKey = EditorGUILayout.TextField(label, currentKey);
                return string.Equals(currentKey, nextKey, StringComparison.Ordinal)
                    ? value
                    : Activator.CreateInstance(type, CreateRecordKey(nextKey));
            }

            private static object CreateRecordKey(string key)
            {
                return Activator.CreateInstance(RecordKeyType, key ?? string.Empty);
            }

            private static string GetKeyValue(object key)
            {
                return key == null ? string.Empty : (string)RecordKeyType.GetProperty("Value").GetValue(key, null);
            }

            private static void EnsureAvailable()
            {
                if (!IsAvailable)
                {
                    throw new InvalidOperationException("GameCult.Caching and GameCult.Caching.MessagePack are not loaded.");
                }
            }

            private static Type FindType(string fullName)
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                    .Where(assembly => !assembly.IsDynamic)
                    .Select(assembly => assembly.GetType(fullName, false))
                    .FirstOrDefault(type => type != null);
            }
        }

        private sealed class DescriptorInfo
        {
            public object Raw;
            public Type DocumentType;
            public string SchemaName;
            public CatalogMemberInfo[] Members;

            public static DescriptorInfo From(object raw)
            {
                var type = raw.GetType();
                var catalog = type.GetMethod("ToCatalogEntry").Invoke(raw, null);
                var members = (IEnumerable)catalog.GetType().GetProperty("Members").GetValue(catalog, null);
                return new DescriptorInfo
                {
                    Raw = raw,
                    DocumentType = (Type)type.GetProperty("DocumentType").GetValue(raw, null),
                    SchemaName = (string)type.GetProperty("SchemaName").GetValue(raw, null),
                    Members = members.Cast<object>().Select(CatalogMemberInfo.From).ToArray()
                };
            }
        }

        private sealed class CatalogMemberInfo
        {
            public string MemberName;
            public int Slot;
            public bool IsName;

            public static CatalogMemberInfo From(object raw)
            {
                var type = raw.GetType();
                return new CatalogMemberInfo
                {
                    MemberName = (string)type.GetProperty("MemberName").GetValue(raw, null),
                    Slot = (int)type.GetProperty("Slot").GetValue(raw, null),
                    IsName = (bool)type.GetProperty("IsName").GetValue(raw, null)
                };
            }
        }

        private sealed class RecordInfo
        {
            public string Key;
            public string StoredAt;
            public DescriptorInfo Descriptor;
            public object Document;

            public static RecordInfo From(object raw)
            {
                var type = raw.GetType();
                var key = type.GetProperty("Key").GetValue(raw, null);
                return new RecordInfo
                {
                    Key = (string)key.GetType().GetProperty("Value").GetValue(key, null),
                    StoredAt = (string)type.GetProperty("StoredAt").GetValue(raw, null),
                    Descriptor = DescriptorInfo.From(type.GetProperty("Descriptor").GetValue(raw, null)),
                    Document = type.GetProperty("Document").GetValue(raw, null)
                };
            }
        }
    }
}
