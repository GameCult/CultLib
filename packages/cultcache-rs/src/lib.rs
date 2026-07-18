use anyhow::Context;
use anyhow::Result;
use anyhow::anyhow;
use serde::Serialize;
use serde::de::DeserializeOwned;
use std::collections::BTreeMap;
use std::collections::BTreeSet;
use std::ffi::OsStr;
use std::fmt;
use std::fs;
use std::fs::File;
use std::fs::OpenOptions;
use std::io::Write;
use std::path::Path;
use std::path::PathBuf;

extern crate self as cultcache_rs;

pub use cultcache_rs_derive::DatabaseEntry;

pub trait DatabaseEntry: Serialize + DeserializeOwned + Clone + Send + 'static {
    const TYPE: &'static str;
    const SCHEMA_NAME: &'static str = "DatabaseEntry";
}

pub trait CultCacheRegistry {
    fn register_entries(&self, cache: &mut CultCache) -> Result<()>;
}

#[macro_export]
macro_rules! cultcache_registry {
    ($name:ident { $($entry:ty),* $(,)? }) => {
        #[derive(Clone, Copy, Debug, Default)]
        pub struct $name;

        impl $crate::CultCacheRegistry for $name {
            fn register_entries(&self, cache: &mut $crate::CultCache) -> ::anyhow::Result<()> {
                $(
                    cache.register_entry_type::<$entry>()?;
                )*
                Ok(())
            }
        }
    };
}

#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultCacheEnvelope {
    pub key: String,
    #[serde(rename = "type")]
    pub r#type: String,
    #[serde(with = "serde_bytes")]
    pub payload: Vec<u8>,
    pub stored_at: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schema_id: Option<String>,
}

#[derive(Clone, Debug, serde::Serialize, serde::Deserialize)]
struct PersistedStoreSnapshot(
    String,
    Vec<PersistedSchemaCatalogEntry>,
    Vec<PersistedRecord>,
);

#[derive(Clone, Debug, serde::Serialize, serde::Deserialize)]
struct PersistedSchemaCatalogEntry(
    String,
    String,
    String,
    String,
    String,
    Vec<String>,
    Vec<PersistedSchemaCatalogMember>,
);

#[derive(Clone, Debug, serde::Serialize, serde::Deserialize)]
struct PersistedSchemaCatalogMember(
    u32,
    String,
    String,
    bool,
    bool,
    Option<String>,
    bool,
    Option<String>,
);

#[derive(Clone, Debug, serde::Serialize, serde::Deserialize)]
struct PersistedRecord(
    String,
    String,
    String,
    #[serde(with = "serde_bytes")] Vec<u8>,
);

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PushAllOptions {
    pub soft: bool,
}

/// A complete new snapshot replaced the committed pathname, but its final
/// durability barrier failed. Callers must fail-stop or reopen and reconcile;
/// retrying as though the write never committed can duplicate higher-level work.
#[derive(Debug)]
pub struct CommittedSnapshotDurabilityUncertain {
    path: PathBuf,
    detail: String,
}

impl CommittedSnapshotDurabilityUncertain {
    fn new(path: PathBuf, error: impl fmt::Display) -> Self {
        Self {
            path,
            detail: error.to_string(),
        }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn detail(&self) -> &str {
        &self.detail
    }
}

impl fmt::Display for CommittedSnapshotDurabilityUncertain {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            formatter,
            "snapshot {} was committed but its durability is uncertain: {}",
            self.path.display(),
            self.detail
        )
    }
}

impl std::error::Error for CommittedSnapshotDurabilityUncertain {}

pub trait CacheBackingStore: Send {
    fn pull_all(&self) -> Result<Vec<CultCacheEnvelope>>;
    fn push(&mut self, entry: &CultCacheEnvelope) -> Result<()>;
    fn delete(&mut self, entry: &CultCacheEnvelope) -> Result<()>;

    fn push_all(&mut self, entries: &[CultCacheEnvelope], _options: PushAllOptions) -> Result<()> {
        let existing = self.pull_all()?;
        for entry in existing {
            self.delete(&entry)?;
        }
        for entry in entries {
            self.push(entry)?;
        }
        Ok(())
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SingleFileMessagePackBackingStore {
    path: PathBuf,
}

impl SingleFileMessagePackBackingStore {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into() }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    fn read_all_unlocked(&self) -> Result<Vec<CultCacheEnvelope>> {
        if !self.path.exists() {
            return Ok(Vec::new());
        }
        let bytes = fs::read(&self.path)
            .with_context(|| format!("failed to read {}", self.path.display()))?;
        if bytes.is_empty() {
            return Ok(Vec::new());
        }
        decode_store_snapshot(&bytes).or_else(|_| {
            rmp_serde::from_slice(&bytes)
                .with_context(|| format!("failed to decode MessagePack {}", self.path.display()))
        })
    }

    fn write_all_unlocked(&self, entries: &[CultCacheEnvelope]) -> Result<()> {
        self.write_all_unlocked_with_observer(entries, |_| Ok(()))
    }

    fn write_all_unlocked_with_observer(
        &self,
        entries: &[CultCacheEnvelope],
        mut observe: impl FnMut(SnapshotReplacementStage) -> Result<()>,
    ) -> Result<()> {
        if let Some(parent) = self.path.parent() {
            fs::create_dir_all(parent)
                .with_context(|| format!("failed to create {}", parent.display()))?;
        }
        remove_stale_candidates(&self.path)?;
        let bytes = rmp_serde::to_vec(&encode_store_snapshot(entries))
            .context("failed to encode MessagePack")?;
        let tmp_path = temporary_path_for(&self.path);
        let mut candidate = OpenOptions::new()
            .create_new(true)
            .write(true)
            .open(&tmp_path)
            .with_context(|| format!("failed to open {}", tmp_path.display()))?;
        candidate
            .write_all(&bytes)
            .with_context(|| format!("failed to write {}", tmp_path.display()))?;
        candidate
            .sync_all()
            .with_context(|| format!("failed to flush {}", tmp_path.display()))?;
        drop(candidate);

        observe(SnapshotReplacementStage::CandidateSynced)?;
        replace_file(&tmp_path, &self.path)?;
        if let Err(error) = observe(SnapshotReplacementStage::TargetReplaced) {
            return Err(anyhow::Error::new(
                CommittedSnapshotDurabilityUncertain::new(self.path.clone(), error),
            ));
        }

        if let Some(parent) = self.path.parent() {
            if let Err(error) = sync_parent_directory(parent) {
                return Err(anyhow::Error::new(
                    CommittedSnapshotDurabilityUncertain::new(self.path.clone(), error),
                ));
            }
        }
        Ok(())
    }

    fn with_shared_lock<T>(&self, action: impl FnOnce() -> Result<T>) -> Result<T> {
        let lock = self.open_lock_file()?;
        fs2::FileExt::lock_shared(&lock)
            .with_context(|| format!("failed to lock {}", self.lock_path().display()))?;
        let result = action();
        fs2::FileExt::unlock(&lock)
            .with_context(|| format!("failed to unlock {}", self.lock_path().display()))?;
        result
    }

    fn with_exclusive_lock<T>(&self, action: impl FnOnce() -> Result<T>) -> Result<T> {
        self.with_exclusive_lock_using_unlock(action, |lock| fs2::FileExt::unlock(lock))
    }

    fn with_exclusive_lock_using_unlock<T>(
        &self,
        action: impl FnOnce() -> Result<T>,
        unlock: impl FnOnce(&File) -> std::io::Result<()>,
    ) -> Result<T> {
        let lock = self.open_lock_file()?;
        fs2::FileExt::lock_exclusive(&lock)
            .with_context(|| format!("failed to lock {}", self.lock_path().display()))?;
        let action_result = action();

        // Closing the file handle releases the OS lock even when an explicit
        // unlock reports an error. The action outcome remains authoritative:
        // in particular, never replace a committed-durability-uncertain error
        // with secondary lock-cleanup noise.
        let _unlock_result = unlock(&lock);
        drop(lock);
        action_result
    }

    fn open_lock_file(&self) -> Result<File> {
        let lock_path = self.lock_path();
        if let Some(parent) = lock_path.parent() {
            fs::create_dir_all(parent)
                .with_context(|| format!("failed to create {}", parent.display()))?;
        }
        OpenOptions::new()
            .create(true)
            .read(true)
            .truncate(false)
            .write(true)
            .open(&lock_path)
            .with_context(|| format!("failed to open {}", lock_path.display()))
    }

    fn lock_path(&self) -> PathBuf {
        let mut lock_name = self
            .path
            .file_name()
            .map(|value| value.to_os_string())
            .unwrap_or_else(|| "cultcache.msgpack".into());
        lock_name.push(".lock");
        self.path.with_file_name(lock_name)
    }
}

impl CacheBackingStore for SingleFileMessagePackBackingStore {
    fn pull_all(&self) -> Result<Vec<CultCacheEnvelope>> {
        self.with_shared_lock(|| self.read_all_unlocked())
    }

    fn push(&mut self, entry: &CultCacheEnvelope) -> Result<()> {
        self.with_exclusive_lock(|| {
            let mut entries = self.read_all_unlocked()?;
            entries.retain(|candidate| entry_id(candidate) != entry_id(entry));
            entries.push(entry.clone());
            entries.sort_by_key(entry_id);
            self.write_all_unlocked(&entries)
        })
    }

    fn delete(&mut self, entry: &CultCacheEnvelope) -> Result<()> {
        self.with_exclusive_lock(|| {
            let mut entries = self.read_all_unlocked()?;
            entries.retain(|candidate| entry_id(candidate) != entry_id(entry));
            self.write_all_unlocked(&entries)
        })
    }

    fn push_all(&mut self, entries: &[CultCacheEnvelope], _options: PushAllOptions) -> Result<()> {
        self.with_exclusive_lock(|| {
            let mut entries = entries.to_vec();
            entries.sort_by_key(entry_id);
            self.write_all_unlocked(&entries)
        })
    }
}

struct CultCacheStoreRegistration {
    store: Box<dyn CacheBackingStore>,
    types: BTreeSet<String>,
}

pub struct CultCache {
    definitions: BTreeMap<String, &'static str>,
    schema_name_definitions: BTreeMap<String, String>,
    entries: BTreeMap<String, CultCacheEnvelope>,
    stores: Vec<CultCacheStoreRegistration>,
}

impl CultCache {
    pub fn new() -> Self {
        Self {
            definitions: BTreeMap::new(),
            schema_name_definitions: BTreeMap::new(),
            entries: BTreeMap::new(),
            stores: Vec::new(),
        }
    }

    pub fn register_entry_type<T: DatabaseEntry>(&mut self) -> Result<()> {
        if T::TYPE.trim().is_empty() {
            return Err(anyhow!(
                "CultCache entry types must declare a non-empty type"
            ));
        }
        if let Some(existing_schema) = self.definitions.get(T::TYPE)
            && *existing_schema != T::SCHEMA_NAME
        {
            return Err(anyhow!(
                "CultCache already has a different definition registered for type {:?}",
                T::TYPE
            ));
        }
        if let Some(existing_type) = self.schema_name_definitions.get(T::SCHEMA_NAME)
            && existing_type != T::TYPE
        {
            return Err(anyhow!(
                "CultCache schema name {:?} is already registered for type {:?}",
                T::SCHEMA_NAME,
                existing_type
            ));
        }
        self.definitions.insert(T::TYPE.to_string(), T::SCHEMA_NAME);
        self.schema_name_definitions
            .insert(T::SCHEMA_NAME.to_string(), T::TYPE.to_string());
        Ok(())
    }

    pub fn register_document_type<T: DatabaseEntry>(&mut self) -> Result<()> {
        self.register_entry_type::<T>()
    }

    pub fn register_registry<R: CultCacheRegistry>(&mut self, registry: R) -> Result<&mut Self> {
        registry.register_entries(self)?;
        Ok(self)
    }

    pub fn add_backing_store(
        &mut self,
        store: impl CacheBackingStore + 'static,
        types: impl IntoIterator<Item = impl Into<String>>,
    ) {
        self.stores.push(CultCacheStoreRegistration {
            store: Box::new(store),
            types: types.into_iter().map(Into::into).collect(),
        });
    }

    pub fn add_generic_backing_store(&mut self, store: impl CacheBackingStore + 'static) {
        self.add_backing_store(store, Vec::<String>::new());
    }

    pub fn pull_all_backing_stores(&mut self) -> Result<()> {
        self.entries.clear();
        let known_types: BTreeSet<String> = self.definitions.keys().cloned().collect();
        let schema_name_definitions = self.schema_name_definitions.clone();
        for registration in &mut self.stores {
            for mut entry in registration.store.pull_all()? {
                let Some(canonical_type) = resolve_registered_type(
                    &known_types,
                    &schema_name_definitions,
                    &entry.r#type,
                ) else {
                    return Err(anyhow!(
                        "No schema is registered for persisted entry type {:?}",
                        entry.r#type
                    ));
                };
                entry.r#type = canonical_type;
                self.entries.insert(entry_id(&entry), entry);
            }
        }
        Ok(())
    }

    pub fn get<T: DatabaseEntry>(&self, key: &str) -> Result<Option<T>> {
        self.require_entry_type::<T>()?;
        let Some(entry) = self.entries.get(&entry_id_parts(T::TYPE, key)) else {
            return Ok(None);
        };
        let payload = rmp_serde::from_slice(&entry.payload).with_context(|| {
            format!(
                "failed to decode CultCache entry {:?} at key {:?} as {}",
                T::TYPE,
                key,
                T::SCHEMA_NAME
            )
        })?;
        Ok(Some(payload))
    }

    pub fn get_required<T: DatabaseEntry>(&self, key: &str) -> Result<T> {
        self.get::<T>(key)?
            .ok_or_else(|| anyhow!("CultCache has no {:?} entry at key {:?}", T::TYPE, key))
    }

    pub fn get_envelope<T: DatabaseEntry>(&self, key: &str) -> Result<Option<CultCacheEnvelope>> {
        self.require_entry_type::<T>()?;
        Ok(self.entries.get(&entry_id_parts(T::TYPE, key)).cloned())
    }

    pub fn get_required_envelope<T: DatabaseEntry>(&self, key: &str) -> Result<CultCacheEnvelope> {
        self.get_envelope::<T>(key)?
            .ok_or_else(|| anyhow!("CultCache has no {:?} envelope at key {:?}", T::TYPE, key))
    }

    pub fn get_all<T: DatabaseEntry>(&self) -> Result<Vec<T>> {
        self.require_entry_type::<T>()?;
        let mut values = Vec::new();
        for entry in self.entries.values() {
            if entry.r#type != T::TYPE {
                continue;
            }
            values.push(rmp_serde::from_slice(&entry.payload).with_context(|| {
                format!(
                    "failed to decode CultCache entry {:?} at key {:?} as {}",
                    T::TYPE,
                    entry.key,
                    T::SCHEMA_NAME
                )
            })?);
        }
        Ok(values)
    }

    pub fn put<T: DatabaseEntry>(&mut self, key: impl Into<String>, value: &T) -> Result<T> {
        self.require_entry_type::<T>()?;
        let key = key.into();
        let payload = rmp_serde::to_vec(value).with_context(|| {
            format!(
                "failed to encode CultCache entry {:?} at key {:?} as {}",
                T::TYPE,
                key,
                T::SCHEMA_NAME
            )
        })?;
        let parsed: T = rmp_serde::from_slice(&payload).with_context(|| {
            format!(
                "failed to validate CultCache entry {:?} at key {:?} as {}",
                T::TYPE,
                key,
                T::SCHEMA_NAME
            )
        })?;
        let entry = CultCacheEnvelope {
            key: key.clone(),
            r#type: T::TYPE.to_string(),
            payload,
            stored_at: now_utc_second(),
            schema_id: Some(T::TYPE.to_string()),
        };
        let route = self.resolve_route_indices(T::TYPE);
        let Some(primary_index) = route.first().copied() else {
            return Err(anyhow!(
                "No backing store is registered for entry type {:?}",
                T::TYPE
            ));
        };
        self.stores[primary_index].store.push(&entry)?;
        for mirror_index in route.iter().skip(1).copied() {
            self.stores[mirror_index].store.push(&entry)?;
        }
        self.entries.insert(entry_id(&entry), entry);
        Ok(parsed)
    }

    pub fn put_envelope<T: DatabaseEntry>(&mut self, entry: CultCacheEnvelope) -> Result<T> {
        self.require_entry_type::<T>()?;
        if entry.r#type != T::TYPE {
            return Err(anyhow!(
                "CultCache envelope type {:?} does not match registered Rust type {:?}",
                entry.r#type,
                T::TYPE
            ));
        }
        if entry.key.trim().is_empty() {
            return Err(anyhow!(
                "CultCache envelope keys for type {:?} must be non-empty",
                T::TYPE
            ));
        }
        if entry.stored_at.trim().is_empty() {
            return Err(anyhow!(
                "CultCache envelope stored_at for type {:?} must be non-empty",
                T::TYPE
            ));
        }

        let parsed: T = rmp_serde::from_slice(&entry.payload).with_context(|| {
            format!(
                "failed to validate CultCache envelope {:?} at key {:?} as {}",
                T::TYPE,
                entry.key,
                T::SCHEMA_NAME
            )
        })?;
        let route = self.resolve_route_indices(T::TYPE);
        let Some(primary_index) = route.first().copied() else {
            return Err(anyhow!(
                "No backing store is registered for entry type {:?}",
                T::TYPE
            ));
        };
        self.stores[primary_index].store.push(&entry)?;
        for mirror_index in route.iter().skip(1).copied() {
            self.stores[mirror_index].store.push(&entry)?;
        }
        self.entries.insert(entry_id(&entry), entry);
        Ok(parsed)
    }

    pub fn update<T, F>(&mut self, key: &str, updater: F) -> Result<T>
    where
        T: DatabaseEntry,
        F: FnOnce(Option<T>) -> T,
    {
        let current = self.get::<T>(key)?;
        self.put::<T>(key.to_string(), &updater(current))
    }

    pub fn delete<T: DatabaseEntry>(&mut self, key: &str) -> Result<bool> {
        self.require_entry_type::<T>()?;
        let id = entry_id_parts(T::TYPE, key);
        let Some(entry) = self.entries.get(&id).cloned() else {
            return Ok(false);
        };
        let route = self.resolve_route_indices(T::TYPE);
        let Some(primary_index) = route.first().copied() else {
            return Err(anyhow!(
                "No backing store is registered for entry type {:?}",
                T::TYPE
            ));
        };
        self.stores[primary_index].store.delete(&entry)?;
        for mirror_index in route.iter().skip(1).copied() {
            self.stores[mirror_index].store.delete(&entry)?;
        }
        self.entries.remove(&id);
        Ok(true)
    }

    pub fn snapshot(&self) -> Vec<CultCacheEnvelope> {
        self.entries.values().cloned().collect()
    }

    fn require_entry_type<T: DatabaseEntry>(&self) -> Result<()> {
        match self.definitions.get(T::TYPE) {
            Some(schema_name) if *schema_name == T::SCHEMA_NAME => Ok(()),
            _ => Err(anyhow!(
                "CultCache entry type {:?} is not registered on this cache instance",
                T::TYPE
            )),
        }
    }

    fn resolve_route_indices(&self, type_id: &str) -> Vec<usize> {
        let type_specific: Vec<usize> = self
            .stores
            .iter()
            .enumerate()
            .filter_map(|(index, registration)| {
                registration.types.contains(type_id).then_some(index)
            })
            .collect();
        if !type_specific.is_empty() {
            return type_specific;
        }
        self.stores
            .iter()
            .enumerate()
            .filter_map(|(index, registration)| registration.types.is_empty().then_some(index))
            .collect()
    }

}

impl Default for CultCache {
    fn default() -> Self {
        Self::new()
    }
}

fn entry_id(entry: &CultCacheEnvelope) -> String {
    entry_id_parts(&entry.r#type, &entry.key)
}

fn entry_id_parts(r#type: &str, key: &str) -> String {
    format!("{type}::{key}", type = r#type)
}

fn resolve_registered_type(
    known_types: &BTreeSet<String>,
    schema_name_definitions: &BTreeMap<String, String>,
    persisted_type: &str,
) -> Option<String> {
    if known_types.contains(persisted_type) {
        return Some(persisted_type.to_string());
    }

    schema_name_definitions.get(persisted_type).cloned()
}

fn now_utc_second() -> String {
    chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Secs, true)
}

fn encode_store_snapshot(entries: &[CultCacheEnvelope]) -> PersistedStoreSnapshot {
    let mut schema_names = BTreeSet::<String>::new();
    for entry in entries {
        schema_names.insert(
            entry
                .schema_id
                .clone()
                .unwrap_or_else(|| entry.r#type.clone()),
        );
    }

    let catalog = schema_names
        .into_iter()
        .map(|schema_id| {
            PersistedSchemaCatalogEntry(
                schema_id.clone(),
                schema_id.clone(),
                format!("{schema_id}.v1"),
                schema_id.clone(),
                format!(
                    "{{\"schemaName\":\"{}\",\"schemaVersion\":\"{}.v1\",\"members\":[]}}",
                    escape_json_string(&schema_id),
                    escape_json_string(&schema_id)
                ),
                vec![schema_id],
                Vec::new(),
            )
        })
        .collect();
    let records = entries
        .iter()
        .map(|entry| {
            PersistedRecord(
                entry.key.clone(),
                entry
                    .schema_id
                    .clone()
                    .unwrap_or_else(|| entry.r#type.clone()),
                entry.stored_at.clone(),
                entry.payload.clone(),
            )
        })
        .collect();

    PersistedStoreSnapshot("cultcache.store.v1".to_string(), catalog, records)
}

fn decode_store_snapshot(bytes: &[u8]) -> Result<Vec<CultCacheEnvelope>> {
    let snapshot: PersistedStoreSnapshot =
        rmp_serde::from_slice(bytes).context("failed to decode CultCache v1 snapshot")?;
    if snapshot.0 != "cultcache.store.v1" {
        return Err(anyhow!("unsupported CultCache snapshot {}", snapshot.0));
    }

    let catalog = snapshot
        .1
        .into_iter()
        .map(|entry| (entry.0, entry.1))
        .collect::<BTreeMap<_, _>>();
    snapshot
        .2
        .into_iter()
        .map(|record| {
            let r#type = match catalog.get(&record.1) {
                Some(schema_name) => schema_name.clone(),
                None => {
                    let schema_version = infer_schema_version_from_payload(&record.3).ok_or_else(|| {
                        anyhow!(
                            "CultCache record {:?} references missing schema {:?}",
                            record.0,
                            record.1
                        )
                    })?;
                    infer_schema_name(&schema_version).ok_or_else(|| {
                        anyhow!(
                            "CultCache record {:?} references missing schema {:?}",
                            record.0,
                            record.1
                        )
                    })?
                }
            };
            Ok(CultCacheEnvelope {
                key: record.0,
                r#type,
                stored_at: record.2,
                payload: record.3,
                schema_id: Some(record.1),
            })
        })
        .collect()
}

fn infer_schema_version_from_payload(payload: &[u8]) -> Option<String> {
    let mut offset = 0usize;
    read_array_header(payload, &mut offset)?;
    read_string(payload, &mut offset)
}

fn read_array_header(payload: &[u8], offset: &mut usize) -> Option<u32> {
    let marker = *payload.get(*offset)?;
    *offset += 1;
    match marker {
        0x90..=0x9f => Some((marker & 0x0f) as u32),
        0xdc => {
            let bytes = payload.get(*offset..(*offset + 2))?;
            *offset += 2;
            Some(u16::from_be_bytes([bytes[0], bytes[1]]) as u32)
        }
        0xdd => {
            let bytes = payload.get(*offset..(*offset + 4))?;
            *offset += 4;
            Some(u32::from_be_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]))
        }
        _ => None,
    }
}

fn read_string(payload: &[u8], offset: &mut usize) -> Option<String> {
    let marker = *payload.get(*offset)?;
    *offset += 1;
    let length = match marker {
        0xa0..=0xbf => (marker & 0x1f) as usize,
        0xd9 => {
            let length = *payload.get(*offset)? as usize;
            *offset += 1;
            length
        }
        0xda => {
            let bytes = payload.get(*offset..(*offset + 2))?;
            *offset += 2;
            u16::from_be_bytes([bytes[0], bytes[1]]) as usize
        }
        0xdb => {
            let bytes = payload.get(*offset..(*offset + 4))?;
            *offset += 4;
            u32::from_be_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]) as usize
        }
        _ => return None,
    };

    let bytes = payload.get(*offset..(*offset + length))?;
    std::str::from_utf8(bytes).ok().map(str::to_string)
}

fn infer_schema_name(schema_version: &str) -> Option<String> {
    let marker = schema_version.rfind(".v")?;
    let version = schema_version.get((marker + 2)..)?;
    if marker == 0 || version.is_empty() || !version.bytes().all(|value| value.is_ascii_digit()) {
        return None;
    }

    Some(schema_version[..marker].to_string())
}

fn escape_json_string(value: &str) -> String {
    value.replace('\\', "\\\\").replace('"', "\\\"")
}

fn temporary_path_for(path: &Path) -> PathBuf {
    let mut file_name = path
        .file_name()
        .map(|value| value.to_os_string())
        .unwrap_or_else(|| "cultcache.msgpack".into());
    file_name.push(format!(".{}.tmp", uuid::Uuid::new_v4()));
    path.with_file_name(file_name)
}

fn is_canonical_candidate_uuid(value: &[u8]) -> bool {
    if value.len() != 36 || !value.is_ascii() {
        return false;
    }
    let Ok(value) = std::str::from_utf8(value) else {
        return false;
    };
    let Ok(uuid) = uuid::Uuid::parse_str(value) else {
        return false;
    };
    uuid.hyphenated().to_string() == value
}

#[cfg(unix)]
fn is_owned_candidate_name(target_name: &OsStr, candidate_name: &OsStr) -> bool {
    use std::os::unix::ffi::OsStrExt;

    let target = target_name.as_bytes();
    let candidate = candidate_name.as_bytes();
    let Some(remainder) = candidate.strip_prefix(target) else {
        return false;
    };
    let Some(remainder) = remainder.strip_prefix(b".") else {
        return false;
    };
    let Some(uuid) = remainder.strip_suffix(b".tmp") else {
        return false;
    };
    is_canonical_candidate_uuid(uuid)
}

#[cfg(windows)]
fn is_owned_candidate_name(target_name: &OsStr, candidate_name: &OsStr) -> bool {
    use std::os::windows::ffi::OsStrExt;

    let target = target_name.encode_wide().collect::<Vec<_>>();
    let candidate = candidate_name.encode_wide().collect::<Vec<_>>();
    let Some(remainder) = candidate.strip_prefix(target.as_slice()) else {
        return false;
    };
    let Some(remainder) = remainder.strip_prefix(&[b'.' as u16]) else {
        return false;
    };
    let suffix = [b'.' as u16, b't' as u16, b'm' as u16, b'p' as u16];
    let Some(uuid_wide) = remainder.strip_suffix(&suffix) else {
        return false;
    };
    let Ok(uuid) = uuid_wide
        .iter()
        .map(|value| u8::try_from(*value))
        .collect::<std::result::Result<Vec<_>, _>>()
    else {
        return false;
    };
    is_canonical_candidate_uuid(&uuid)
}

#[cfg(not(any(unix, windows)))]
fn is_owned_candidate_name(target_name: &OsStr, candidate_name: &OsStr) -> bool {
    let (Some(target), Some(candidate)) = (target_name.to_str(), candidate_name.to_str()) else {
        return false;
    };
    let Some(remainder) = candidate.strip_prefix(target) else {
        return false;
    };
    let Some(remainder) = remainder.strip_prefix('.') else {
        return false;
    };
    let Some(uuid) = remainder.strip_suffix(".tmp") else {
        return false;
    };
    is_canonical_candidate_uuid(uuid.as_bytes())
}

fn stale_candidate_paths(path: &Path) -> Result<Vec<PathBuf>> {
    let parent = path.parent().unwrap_or_else(|| Path::new("."));
    if !parent.exists() {
        return Ok(Vec::new());
    }
    let target_name = path
        .file_name()
        .unwrap_or_else(|| OsStr::new("cultcache.msgpack"));
    let mut candidates = Vec::new();
    for entry in
        fs::read_dir(parent).with_context(|| format!("failed to inspect {}", parent.display()))?
    {
        let entry = entry.with_context(|| format!("failed to inspect {}", parent.display()))?;
        let name = entry.file_name();
        if is_owned_candidate_name(target_name, &name) {
            candidates.push(entry.path());
        }
    }
    Ok(candidates)
}

fn remove_stale_candidates(path: &Path) -> Result<()> {
    for candidate in stale_candidate_paths(path)? {
        fs::remove_file(&candidate)
            .with_context(|| format!("failed to remove stale candidate {}", candidate.display()))?;
    }
    Ok(())
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum SnapshotReplacementStage {
    CandidateSynced,
    TargetReplaced,
}

#[cfg(unix)]
fn replace_file(candidate: &Path, target: &Path) -> Result<()> {
    fs::rename(candidate, target).with_context(|| {
        format!(
            "failed to atomically replace {} with {}",
            target.display(),
            candidate.display()
        )
    })
}

#[cfg(windows)]
fn replace_file(candidate: &Path, target: &Path) -> Result<()> {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Storage::FileSystem::{
        MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
    };

    let candidate_wide = candidate
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect::<Vec<_>>();
    let target_wide = target
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect::<Vec<_>>();
    let replaced = unsafe {
        MoveFileExW(
            candidate_wide.as_ptr(),
            target_wide.as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    };
    if replaced == 0 {
        return Err(std::io::Error::last_os_error()).with_context(|| {
            format!(
                "failed to atomically replace {} with {}",
                target.display(),
                candidate.display()
            )
        });
    }
    Ok(())
}

#[cfg(not(any(unix, windows)))]
fn replace_file(candidate: &Path, target: &Path) -> Result<()> {
    fs::rename(candidate, target).with_context(|| {
        format!(
            "failed to replace {} with {}",
            target.display(),
            candidate.display()
        )
    })
}

#[cfg(unix)]
fn sync_parent_directory(parent: &Path) -> Result<()> {
    File::open(parent)
        .with_context(|| format!("failed to open directory {}", parent.display()))?
        .sync_all()
        .with_context(|| format!("failed to flush directory {}", parent.display()))
}

#[cfg(not(unix))]
fn sync_parent_directory(_parent: &Path) -> Result<()> {
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use pretty_assertions::assert_eq;

    #[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
    #[cultcache(type = "settings")]
    struct Settings {
        #[cultcache(key = 0)]
        theme: String,
        #[cultcache(key = 1, default)]
        retries: u32,
    }

    #[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
    #[cultcache(type = "note")]
    struct Note {
        #[cultcache(key = 0)]
        title: String,
        #[cultcache(key = 1)]
        body: String,
    }

    #[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
    #[cultcache(type = "runtime-policy", schema = "tests.schema_stamped_entry")]
    struct SchemaStamped {
        #[cultcache(key = 0)]
        schema_version: String,
        #[cultcache(key = 1)]
        name: String,
        #[cultcache(key = 2)]
        value: String,
    }

    cultcache_registry!(TestEntries { Settings, Note });

    fn test_envelope(value: &str) -> CultCacheEnvelope {
        CultCacheEnvelope {
            key: "state".to_string(),
            r#type: "test-state".to_string(),
            payload: value.as_bytes().to_vec(),
            stored_at: "2026-07-15T00:00:00Z".to_string(),
            schema_id: Some("tests.test_state.v1".to_string()),
        }
    }

    #[test]
    fn interruption_before_replacement_preserves_committed_snapshot() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let store_path = temp.path().join("cache.msgpack");
        let mut store = SingleFileMessagePackBackingStore::new(&store_path);
        let old = test_envelope("old");
        let new = test_envelope("new");
        store.push_all(std::slice::from_ref(&old), PushAllOptions::default())?;

        let result = store.write_all_unlocked_with_observer(std::slice::from_ref(&new), |stage| {
            assert_eq!(stage, SnapshotReplacementStage::CandidateSynced);
            assert!(
                store_path.exists(),
                "the committed pathname must never be removed"
            );
            Err(anyhow!("simulated interruption before replacement"))
        });

        assert!(result.is_err());
        let recovered = store.pull_all()?;
        assert_eq!(recovered.len(), 1);
        assert_eq!(recovered[0].payload, old.payload);
        assert_eq!(stale_candidate_paths(&store_path)?.len(), 1);

        store.push_all(std::slice::from_ref(&new), PushAllOptions::default())?;
        let recovered = store.pull_all()?;
        assert_eq!(recovered.len(), 1);
        assert_eq!(recovered[0].payload, new.payload);
        assert!(stale_candidate_paths(&store_path)?.is_empty());
        Ok(())
    }

    #[test]
    fn interruption_after_replacement_recovers_new_snapshot() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let store_path = temp.path().join("cache.msgpack");
        let mut store = SingleFileMessagePackBackingStore::new(&store_path);
        let old = test_envelope("old");
        let new = test_envelope("new");
        store.push_all(std::slice::from_ref(&old), PushAllOptions::default())?;

        let result = store.write_all_unlocked_with_observer(std::slice::from_ref(&new), |stage| {
            if stage == SnapshotReplacementStage::TargetReplaced {
                return Err(anyhow!("simulated interruption after replacement"));
            }
            Ok(())
        });

        let error = result.expect_err("the injected post-replacement failure must surface");
        let uncertain = error
            .downcast_ref::<CommittedSnapshotDurabilityUncertain>()
            .expect("post-replacement failures must be typed as committed but uncertain");
        assert_eq!(uncertain.path(), store_path.as_path());
        assert!(uncertain.detail().contains("simulated interruption"));
        let recovered = store.pull_all()?;
        assert_eq!(recovered.len(), 1);
        assert_eq!(recovered[0].payload, new.payload);
        assert!(stale_candidate_paths(&store_path)?.is_empty());
        Ok(())
    }

    #[test]
    fn first_write_interruption_before_replacement_leaves_store_uncommitted() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let store_path = temp.path().join("cache.msgpack");
        let store = SingleFileMessagePackBackingStore::new(&store_path);
        let new = test_envelope("new");

        let result = store.write_all_unlocked_with_observer(std::slice::from_ref(&new), |stage| {
            assert_eq!(stage, SnapshotReplacementStage::CandidateSynced);
            Err(anyhow!("simulated interruption before first replacement"))
        });

        assert!(result.is_err());
        assert!(!store_path.exists());
        assert!(store.pull_all()?.is_empty());
        assert_eq!(stale_candidate_paths(&store_path)?.len(), 1);
        Ok(())
    }

    #[test]
    fn first_write_interruption_after_replacement_exposes_complete_snapshot() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let store_path = temp.path().join("cache.msgpack");
        let store = SingleFileMessagePackBackingStore::new(&store_path);
        let new = test_envelope("new");

        let result = store.write_all_unlocked_with_observer(std::slice::from_ref(&new), |stage| {
            if stage == SnapshotReplacementStage::TargetReplaced {
                return Err(anyhow!("simulated interruption after first replacement"));
            }
            Ok(())
        });

        let error = result.expect_err("the injected post-replacement failure must surface");
        assert!(
            error
                .downcast_ref::<CommittedSnapshotDurabilityUncertain>()
                .is_some()
        );
        let recovered = store.pull_all()?;
        assert_eq!(recovered.len(), 1);
        assert_eq!(recovered[0].payload, new.payload);
        assert!(stale_candidate_paths(&store_path)?.is_empty());
        Ok(())
    }

    #[test]
    fn unlock_failure_never_masks_the_action_outcome() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let store_path = temp.path().join("cache.msgpack");
        let store = SingleFileMessagePackBackingStore::new(&store_path);

        let completed = store.with_exclusive_lock_using_unlock(
            || Ok(42_u32),
            |_| Err(std::io::Error::other("simulated unlock failure")),
        )?;
        assert_eq!(completed, 42);

        let uncertain: Result<()> = store.with_exclusive_lock_using_unlock(
            || {
                Err(anyhow::Error::new(
                    CommittedSnapshotDurabilityUncertain::new(
                        store_path.clone(),
                        "primary post-commit failure",
                    ),
                ))
            },
            |_| Err(std::io::Error::other("secondary unlock failure")),
        );
        let error = uncertain.expect_err("the action error must survive unlock cleanup");
        let error = error
            .downcast_ref::<CommittedSnapshotDurabilityUncertain>()
            .expect("the primary typed outcome must not be masked");
        assert!(error.detail().contains("primary post-commit failure"));
        assert!(!error.detail().contains("secondary unlock failure"));

        // Both injected unlock failures still close their lock handles. A new
        // exclusive operation must therefore acquire the lock normally.
        store.with_exclusive_lock(|| Ok(()))?;
        Ok(())
    }

    #[test]
    fn stale_cleanup_preserves_non_uuid_lookalikes() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let store_path = temp.path().join("cache.msgpack");
        let lookalike = temp.path().join("cache.msgpack.not-a-uuid.tmp");
        fs::write(&lookalike, b"belongs to somebody else")?;

        let mut store = SingleFileMessagePackBackingStore::new(&store_path);
        store.push_all(&[test_envelope("new")], PushAllOptions::default())?;

        assert_eq!(fs::read(&lookalike)?, b"belongs to somebody else");
        assert!(stale_candidate_paths(&store_path)?.is_empty());
        Ok(())
    }

    #[cfg(unix)]
    #[test]
    fn stale_candidate_symlink_is_removed_without_clobbering_its_target() -> Result<()> {
        use std::os::unix::fs::symlink;

        let temp = tempfile::tempdir()?;
        let store_path = temp.path().join("cache.msgpack");
        let victim_path = temp.path().join("victim.txt");
        fs::write(&victim_path, b"untouched")?;
        let stale_path = temporary_path_for(&store_path);
        symlink(&victim_path, &stale_path)?;

        let mut store = SingleFileMessagePackBackingStore::new(&store_path);
        store.push_all(&[test_envelope("new")], PushAllOptions::default())?;

        assert_eq!(fs::read(&victim_path)?, b"untouched");
        assert!(!stale_path.exists());
        assert!(stale_candidate_paths(&store_path)?.is_empty());
        Ok(())
    }

    #[cfg(unix)]
    #[test]
    fn non_utf8_target_names_recover_their_own_candidates() -> Result<()> {
        use std::ffi::OsString;
        use std::os::unix::ffi::OsStringExt;

        let temp = tempfile::tempdir()?;
        let store_name = OsString::from_vec(b"cache-\xff.msgpack".to_vec());
        let store_path = temp.path().join(store_name);
        let mut store = SingleFileMessagePackBackingStore::new(&store_path);
        let new = test_envelope("new");

        let interrupted = store
            .write_all_unlocked_with_observer(std::slice::from_ref(&new), |_| {
                Err(anyhow!("simulated interruption"))
            });
        assert!(interrupted.is_err());
        assert_eq!(stale_candidate_paths(&store_path)?.len(), 1);

        store.push_all(std::slice::from_ref(&new), PushAllOptions::default())?;
        assert!(stale_candidate_paths(&store_path)?.is_empty());
        assert_eq!(store.pull_all()?[0].payload, new.payload);
        Ok(())
    }

    #[test]
    fn two_contending_writers_leave_one_complete_snapshot() -> Result<()> {
        use std::sync::{Arc, Barrier};

        let temp = tempfile::tempdir()?;
        let store_path = temp.path().join("cache.msgpack");
        let barrier = Arc::new(Barrier::new(3));
        let spawn_writer = |marker: u8| {
            let store_path = store_path.clone();
            let barrier = Arc::clone(&barrier);
            std::thread::spawn(move || -> Result<()> {
                let mut entries = Vec::new();
                for index in 0..128 {
                    let mut entry = test_envelope(std::str::from_utf8(&[marker]).unwrap());
                    entry.key = format!("state-{index:03}");
                    entry.payload = vec![marker; 4096];
                    entries.push(entry);
                }
                let mut store = SingleFileMessagePackBackingStore::new(store_path);
                barrier.wait();
                store.push_all(&entries, PushAllOptions::default())
            })
        };

        let writer_a = spawn_writer(b'a');
        let writer_b = spawn_writer(b'b');
        barrier.wait();
        writer_a.join().expect("writer A panicked")?;
        writer_b.join().expect("writer B panicked")?;

        let recovered = SingleFileMessagePackBackingStore::new(&store_path).pull_all()?;
        assert_eq!(recovered.len(), 128);
        let winner = recovered[0].payload[0];
        assert!(winner == b'a' || winner == b'b');
        assert!(
            recovered
                .iter()
                .all(|entry| entry.payload == vec![winner; 4096])
        );
        assert!(stale_candidate_paths(&store_path)?.is_empty());
        Ok(())
    }

    #[test]
    fn familiar_cultcache_flow_persists_and_reloads_typed_documents() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let store_path = temp.path().join("cache.msgpack");
        let settings = Settings {
            theme: "ash".to_string(),
            retries: 3,
        };

        let mut cache = CultCache::new();
        cache.register_entry_type::<Settings>()?;
        cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&store_path));
        cache.pull_all_backing_stores()?;
        cache.put("app", &settings)?;
        assert_eq!(cache.get_required::<Settings>("app")?, settings);

        let mut reloaded = CultCache::new();
        reloaded.register_entry_type::<Settings>()?;
        reloaded.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&store_path));
        reloaded.pull_all_backing_stores()?;
        assert_eq!(reloaded.get_required::<Settings>("app")?, settings);
        Ok(())
    }

    #[test]
    fn entry_identity_is_polymorphic_by_type_and_key() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let store_path = temp.path().join("cache.msgpack");
        let mut cache = CultCache::new();
        cache.register_registry(TestEntries)?;
        cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&store_path));

        cache.put(
            "shared",
            &Settings {
                theme: "green".to_string(),
                retries: 1,
            },
        )?;
        cache.put(
            "shared",
            &Note {
                title: "same key".to_string(),
                body: "different type".to_string(),
            },
        )?;

        assert_eq!(cache.snapshot().len(), 2);
        assert_eq!(cache.get_required::<Note>("shared")?.title, "same key");
        assert_eq!(cache.get_required::<Settings>("shared")?.theme, "green");
        Ok(())
    }

    #[test]
    fn type_specific_store_routes_before_generic_store() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let generic_path = temp.path().join("generic.msgpack");
        let settings_path = temp.path().join("settings.msgpack");
        let mut cache = CultCache::new();
        cache.register_entry_type::<Settings>()?;
        cache.register_entry_type::<Note>()?;
        cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&generic_path));
        cache.add_backing_store(
            SingleFileMessagePackBackingStore::new(&settings_path),
            ["settings"],
        );

        cache.put(
            "app",
            &Settings {
                theme: "ash".to_string(),
                retries: 3,
            },
        )?;
        cache.put(
            "memo",
            &Note {
                title: "hello".to_string(),
                body: "world".to_string(),
            },
        )?;

        let generic_entries = SingleFileMessagePackBackingStore::new(&generic_path).pull_all()?;
        let settings_entries = SingleFileMessagePackBackingStore::new(&settings_path).pull_all()?;
        assert_eq!(generic_entries[0].r#type, "note");
        assert_eq!(settings_entries[0].r#type, "settings");
        Ok(())
    }

    #[test]
    fn update_and_delete_follow_the_cache_api() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let store_path = temp.path().join("cache.msgpack");
        let mut cache = CultCache::new();
        cache.register_entry_type::<Settings>()?;
        cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&store_path));

        let updated = cache.update::<Settings, _>("app", |current| {
            let mut current = current.unwrap_or(Settings {
                theme: "ash".to_string(),
                retries: 0,
            });
            current.retries += 1;
            current
        })?;
        assert_eq!(updated.retries, 1);
        assert!(cache.delete::<Settings>("app")?);
        assert!(cache.get::<Settings>("app")?.is_none());
        Ok(())
    }

    #[test]
    fn pull_rejects_unregistered_persisted_entry_type() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let store_path = temp.path().join("cache.msgpack");
        let mut store = SingleFileMessagePackBackingStore::new(&store_path);
        store.push(&CultCacheEnvelope {
            key: "unknown".to_string(),
            r#type: "unregistered".to_string(),
            payload: rmp_serde::to_vec(&1_u8)?,
            stored_at: now_utc_second(),
            schema_id: Some("unregistered".to_string()),
        })?;

        let mut cache = CultCache::new();
        cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&store_path));
        let error = cache.pull_all_backing_stores().unwrap_err();
        assert!(
            error
                .to_string()
                .contains("No schema is registered for persisted entry type")
        );
        Ok(())
    }

    #[test]
    fn payload_is_binary_messagepack_not_json_value() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let store_path = temp.path().join("cache.msgpack");
        let mut cache = CultCache::new();
        cache.register_entry_type::<Settings>()?;
        cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&store_path));
        cache.put(
            "app",
            &Settings {
                theme: "ash".to_string(),
                retries: 3,
            },
        )?;

        let entry = cache.snapshot().remove(0);
        let decoded: Settings = rmp_serde::from_slice(&entry.payload)?;
        assert_eq!(decoded.theme, "ash");
        assert!(!entry.payload.is_empty());
        Ok(())
    }

    #[test]
    fn corrupted_payload_fails_during_typed_retrieval() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let store_path = temp.path().join("cache.msgpack");
        let mut store = SingleFileMessagePackBackingStore::new(&store_path);
        store.push(&CultCacheEnvelope {
            key: "app".to_string(),
            r#type: "settings".to_string(),
            payload: vec![0xc1],
            stored_at: now_utc_second(),
            schema_id: Some("settings".to_string()),
        })?;

        let mut cache = CultCache::new();
        cache.register_entry_type::<Settings>()?;
        cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&store_path));
        cache.pull_all_backing_stores()?;
        let error = cache.get_required::<Settings>("app").unwrap_err();
        assert!(
            error
                .to_string()
                .contains("failed to decode CultCache entry")
        );
        Ok(())
    }

    #[test]
    fn put_envelope_reuses_existing_messagepack_payload() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let origin_store = temp.path().join("origin.msgpack");
        let target_store = temp.path().join("target.msgpack");

        let mut origin = CultCache::new();
        origin.register_entry_type::<Settings>()?;
        origin.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&origin_store));
        origin.put(
            "app",
            &Settings {
                theme: "ash".to_string(),
                retries: 3,
            },
        )?;

        let envelope = origin.get_required_envelope::<Settings>("app")?;

        let mut target = CultCache::new();
        target.register_entry_type::<Settings>()?;
        target.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&target_store));
        let applied = target.put_envelope::<Settings>(envelope.clone())?;

        assert_eq!(
            applied,
            Settings {
                theme: "ash".to_string(),
                retries: 3,
            }
        );
        assert_eq!(target.get_required::<Settings>("app")?, applied);
        assert_eq!(
            target.get_required_envelope::<Settings>("app")?.payload,
            envelope.payload
        );
        Ok(())
    }

    #[test]
    fn messagepack_store_recovers_schema_stamped_records_missing_catalog_entries() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let store_path = temp.path().join("missing-catalog.msgpack");
        let expected = SchemaStamped {
            schema_version: "tests.schema_stamped_entry.v1".to_string(),
            name: "schema-stamped".to_string(),
            value: "still readable".to_string(),
        };
        let snapshot = PersistedStoreSnapshot(
            "cultcache.store.v1".to_string(),
            Vec::new(),
            vec![PersistedRecord(
                "record-1".to_string(),
                "sha256:stale-schema-id-from-cold-record".to_string(),
                "2026-06-25T12:00:00Z".to_string(),
                rmp_serde::to_vec(&expected)?,
            )],
        );
        fs::write(&store_path, rmp_serde::to_vec(&snapshot)?)?;

        let mut cache = CultCache::new();
        cache.register_entry_type::<SchemaStamped>()?;
        cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&store_path));
        cache.pull_all_backing_stores()?;

        assert_eq!(cache.get_required::<SchemaStamped>("record-1")?, expected);
        assert_eq!(
            cache
                .get_required_envelope::<SchemaStamped>("record-1")?
                .schema_id,
            Some("sha256:stale-schema-id-from-cold-record".to_string())
        );
        Ok(())
    }
}
