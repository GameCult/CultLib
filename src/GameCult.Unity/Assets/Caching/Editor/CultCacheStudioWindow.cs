using System;
using System.IO;
using System.Linq;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using UnityEditor;
using UnityEngine;

namespace GameCult.Unity.Caching.Editor
{
    public sealed class CultCacheStudioWindow : EditorWindow
    {
        private const string LastPathKey = "GameCult.CultCacheStudio.LastPath";

        private CultCache _cache;
        private CultInspectorModel _model;
        private CultInspector _inspector;
        private CultInspectorEdit _edit;
        private CultStoredDocument[] _records = Array.Empty<CultStoredDocument>();
        private string _path = string.Empty;
        private bool _directory;
        private bool _readOnly;
        private string _search = string.Empty;
        private Type _selectedType;
        private string _selectedKey;
        private Vector2 _typeScroll;
        private Vector2 _recordScroll;
        private Vector2 _inspectorScroll;
        private string _status = "Open a CultCache store to begin.";
        private MessageType _statusType = MessageType.Info;

        private bool ReadOnly => _cache.BackingStores.Any(store => store.IsReadOnly);

        private static string ProjectRoot => Path.GetDirectoryName(Path.GetFullPath(Application.dataPath));

        [MenuItem("GameCult/CultCache Studio")]
        public static void Open()
        {
            GetWindow<CultCacheStudioWindow>("CultCache Studio");
        }

        private void OnEnable()
        {
            _path = EditorPrefs.GetString(LastPathKey, string.Empty);
        }

        // Disposing never flushes; unsaved edits are dropped on close and on script reload.
        private void OnDisable()
        {
            CloseStore();
        }

        private void OnGUI()
        {
            DrawToolbar();
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, _statusType);
            if (_cache == null) return;
            if (ReadOnly)
                EditorGUILayout.HelpBox("Read-only store: " + _path + " was opened read-only. Adding, deleting, editing and saving are disabled.", MessageType.Warning);

            _records = _cache.AllStoredDocuments.ToArray();
            _inspector.Records = _records;
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawTypes();
                DrawRecords();
                DrawInspector();
            }
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _path = GUILayout.TextField(_path, EditorStyles.toolbarTextField, GUILayout.MinWidth(180));
                _directory = GUILayout.Toggle(_directory, "Directory", EditorStyles.toolbarButton, GUILayout.Width(62));
                _readOnly = GUILayout.Toggle(_readOnly, "Read Only", EditorStyles.toolbarButton, GUILayout.Width(66));
                if (GUILayout.Button("Open", EditorStyles.toolbarButton, GUILayout.Width(44))) Browse(false);
                using (new EditorGUI.DisabledScope(_readOnly || _directory))
                {
                    if (GUILayout.Button(new GUIContent("New", "Creates a writable single-file store. Turn off Read Only and Directory to enable."),
                            EditorStyles.toolbarButton, GUILayout.Width(40)))
                        Browse(true);
                }

                using (new EditorGUI.DisabledScope(_cache == null))
                {
                    if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(52)) && ConfirmDiscard()) OpenStore(_path, false);
                    using (new EditorGUI.DisabledScope(_cache == null || ReadOnly || !_cache.IsDirty))
                    {
                        if (GUILayout.Button("Save", EditorStyles.toolbarButton, GUILayout.Width(40))) Save();
                    }

                    if (GUILayout.Button("Close", EditorStyles.toolbarButton, GUILayout.Width(44)) && ConfirmDiscard())
                    {
                        CloseStore();
                        SetStatus("Closed " + _path + ".", MessageType.Info);
                        GUIUtility.ExitGUI();
                    }
                }

                GUILayout.FlexibleSpace();
                if (_cache != null)
                    GUILayout.Label(ReadOnly ? "Read Only" : _cache.IsDirty ? "Dirty" : "Saved", EditorStyles.miniBoldLabel);
            }
        }

        private void DrawTypes()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(240)))
            {
                _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);
                var counts = _records.GroupBy(r => r.Descriptor.DocumentType).ToDictionary(g => g.Key, g => g.Count());
                _typeScroll = EditorGUILayout.BeginScrollView(_typeScroll);
                foreach (var descriptor in _cache.Registry.AllDescriptors)
                {
                    var type = descriptor.DocumentType;
                    if (_search.Length > 0 &&
                        descriptor.SchemaName.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0 &&
                        type.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var count = counts.Where(c => type.IsAssignableFrom(c.Key)).Sum(c => c.Value);
                    var label = descriptor.SchemaName + (descriptor.IsGlobal ? count == 0 ? " (absent)" : " (global)" : " (" + count + ")");
                    if (GUILayout.Toggle(_selectedType == type, label, EditorStyles.miniButton) && _selectedType != type)
                    {
                        _selectedType = type;
                        _selectedKey = null;
                    }
                }

                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawRecords()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(260)))
            {
                var descriptor = _selectedType == null ? null : _cache.Registry.GetRequired(_selectedType);
                var selected = Selected();
                var constructible = _selectedType != null && _selectedType.GetConstructor(Type.EmptyTypes) != null;
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(descriptor == null || descriptor.IsGlobal || ReadOnly || !constructible))
                    {
                        if (GUILayout.Button("Add", EditorStyles.miniButtonLeft)) Add();
                    }

                    using (new EditorGUI.DisabledScope(selected == null || selected.Descriptor.IsGlobal || ReadOnly))
                    {
                        if (GUILayout.Button("Duplicate", EditorStyles.miniButtonMid)) Duplicate(selected);
                    }

                    using (new EditorGUI.DisabledScope(selected == null || ReadOnly))
                    {
                        if (GUILayout.Button("Delete", EditorStyles.miniButtonRight)) Delete(selected);
                    }
                }

                _recordScroll = EditorGUILayout.BeginScrollView(_recordScroll);
                if (descriptor == null)
                {
                    EditorGUILayout.LabelField("Select a document type.");
                }
                else
                {
                    var records = _records.Where(r => _selectedType.IsAssignableFrom(r.Descriptor.DocumentType))
                        .Select(r => (Record: r, Label: CultInspectorModel.RecordLabel(r)))
                        .OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (descriptor.IsGlobal && records.Length == 0)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField("Global is absent.");
                            using (new EditorGUI.DisabledScope(ReadOnly || !constructible))
                            {
                                if (GUILayout.Button("Create", GUILayout.Width(56))) Add();
                            }
                        }
                    }

                    foreach (var (record, label) in records)
                    {
                        var text = record.Descriptor.DocumentType == _selectedType ? label : label + "  <" + record.Descriptor.DocumentType.Name + ">";
                        if (GUILayout.Toggle(_selectedKey == record.Key.Value, text, EditorStyles.miniButton)) _selectedKey = record.Key.Value;
                    }
                }

                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawInspector()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
            {
                var record = Selected();
                if (record == null)
                {
                    EditorGUILayout.LabelField("Select a record.");
                    return;
                }

                EditorGUILayout.LabelField(record.Descriptor.SchemaName + "  " + record.Descriptor.DocumentType.Name, EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel("Key " + record.Key.Value + "   Stored " + record.StoredAt, EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                _inspectorScroll = EditorGUILayout.BeginScrollView(_inspectorScroll);

                // Drawers edit the edit's private copy. A commit spends the edit, admitted or refused, and the next frame
                // begins a new one from whatever record the cache then holds. The copy is held here and kept only by a frame
                // that drew to the end with no drawer failing: a claimed drawer that threw, a built-in drawing exception, or
                // an ExitGUIException all leave _edit null, so a half-made in-place mutation is never committed later.
                var edit = _edit != null && _edit.IsFor(record) ? _edit : _model.BeginEdit(record);
                _edit = null;
                var readOnly = ReadOnly;
                using (new EditorGUI.DisabledScope(readOnly))
                {
                    EditorGUI.BeginChangeCheck();
                    var drawn = _inspector.DrawDocument(edit);
                    var changed = EditorGUI.EndChangeCheck();
                    if (drawn)
                    {
                        _edit = edit;
                        if (changed && !readOnly && !edit.Commit(_cache, out var error)) SetStatus(error, MessageType.Error);
                    }
                }

                EditorGUILayout.EndScrollView();
            }
        }

        private CultStoredDocument Selected()
        {
            return _selectedKey == null ? null : _records.FirstOrDefault(r => r.Key.Value == _selectedKey);
        }

        // Dialogs start in the last store's folder, or the project root; never under Assets.
        private void Browse(bool create)
        {
            if (!ConfirmDiscard()) return;
            var last = string.IsNullOrEmpty(_path) ? null : Path.GetDirectoryName(FullPathOrNull(_path) ?? string.Empty);
            var directory = !string.IsNullOrEmpty(last) && Directory.Exists(last) && !InsideAssets(last) ? last : ProjectRoot;
            var path = create
                ? EditorUtility.SaveFilePanel("New CultCache store", directory, "GameData", "cc")
                : EditorUtility.OpenFilePanel("Open CultCache store", directory, _directory ? "" : "cc");
            if (!string.IsNullOrEmpty(path)) OpenStore(path, create);
            GUIUtility.ExitGUI();
        }

        // New writes an empty single-file store outside Assets and never touches an existing path; directory stores and
        // read-only opens are refused because neither writes a store on creation. Game data never lives under Assets:
        // Unity would import and manage it, and CultCache exists so Unity does not manage game data.
        private void OpenStore(string path, bool create)
        {
            var exists = File.Exists(path) || Directory.Exists(DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(path));
            var insideAssets = InsideAssets(path);
            string refusal = null;
            if (create && (_readOnly || _directory))
                refusal = "New creates writable single-file stores only; turn off Read Only and Directory. Nothing was created.";
            else if (create && insideAssets)
                refusal = "New refuses " + path + ": game data does not live under Assets, where Unity imports and manages files, and CultCache exists so Unity does not manage game data. Choose a folder outside " + Application.dataPath + ". Nothing was created.";
            else if (create && exists)
                refusal = path + " already exists; nothing was created or replaced. Use Open to edit it.";
            else if (!create && !File.Exists(path))
                refusal = "No store at " + path + ".";
            if (refusal != null)
            {
                SetStatus(refusal, MessageType.Error);
                return;
            }

            CloseStore();
            try
            {
                _cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { ReadOnly = !create && _readOnly, UseDirectoryStore = !create && _directory });
                if (create)
                {
                    _cache.BackingStores[0].PushAll();
                    if (!File.Exists(path)) throw new IOException("the store wrote no file");
                }

                _model = CultCacheMessagePack.CreateInspectorModel(_cache.Registry);
                _inspector = new CultInspector(_model);
                _path = path;
                EditorPrefs.SetString(LastPathKey, path);
                var opened = (create ? "Created " : "Opened ") + path + (_cache.BackingStores[0] is DirectoryMessagePackBackingStore ? " (directory store)." : ".");
                if (insideAssets)
                    SetStatus(opened + " This store is under Assets, where Unity imports and manages files; game data belongs outside Assets.", MessageType.Warning);
                else
                    SetStatus(opened, MessageType.Info);
            }
            catch (Exception exception)
            {
                CloseStore();
                SetStatus("Failed to open " + path + ": " + exception.GetBaseException().Message, MessageType.Error);
            }
        }

        private static bool InsideAssets(string path)
        {
            var full = FullPathOrNull(path);
            var assets = FullPathOrNull(Application.dataPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full != null && assets != null &&
                   (full.Equals(assets, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        private static string FullPathOrNull(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void CloseStore()
        {
            _cache?.Dispose();
            _cache = null;
            _model = null;
            _inspector = null;
            _edit = null;
            _records = Array.Empty<CultStoredDocument>();
            _selectedKey = null;
        }

        private bool ConfirmDiscard()
        {
            return _cache == null || !_cache.IsDirty ||
                   EditorUtility.DisplayDialog("CultCache Studio", "Discard unsaved changes to " + _path + "?", "Discard", "Cancel");
        }

        private void Save()
        {
            Run("Saved " + _path + ".", () => _cache.FlushAsync().GetAwaiter().GetResult());
        }

        private void Add()
        {
            var type = _selectedType;
            Run("Added " + type.Name + ".", () => _selectedKey = _cache.UpsertAsync(type, Activator.CreateInstance(type)).GetAwaiter().GetResult().Value);
            GUIUtility.ExitGUI();
        }

        private void Duplicate(CultStoredDocument record)
        {
            var type = record.Descriptor.DocumentType;
            Run("Duplicated " + CultInspectorModel.RecordLabel(record) + ".", () =>
                _selectedKey = _cache.UpsertAsync(type, _model.Clone(record.Document, type)).GetAwaiter().GetResult().Value);
            GUIUtility.ExitGUI();
        }

        private void Delete(CultStoredDocument record)
        {
            var label = CultInspectorModel.RecordLabel(record);
            if (!EditorUtility.DisplayDialog("CultCache Studio", "Delete " + label + "?", "Delete", "Cancel")) return;
            Run("Deleted " + label + ".", () =>
            {
                _cache.Remove(record.Key);
                _selectedKey = null;
            });
            GUIUtility.ExitGUI();
        }

        private void Run(string success, Action action)
        {
            try
            {
                action();
                if (success != null) SetStatus(success, MessageType.Info);
            }
            catch (Exception exception)
            {
                SetStatus(exception.GetBaseException().Message, MessageType.Error);
            }
        }

        private void SetStatus(string message, MessageType type)
        {
            _status = message;
            _statusType = type;
            Repaint();
        }
    }
}
