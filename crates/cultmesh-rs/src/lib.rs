use anyhow::Context;
use anyhow::Result;
use anyhow::anyhow;
use cultcache_rs::CultCache;
use cultcache_rs::DatabaseEntry;
use cultcache_rs::SingleFileMessagePackBackingStore;
use cultnet_rs::CultNetDocumentRegistry;
use std::path::Path;

pub trait CultMeshDocumentSet {
    fn register_cache(&self, cache: &mut CultCache) -> Result<()>;
    fn register_documents(&self, registry: &mut CultNetDocumentRegistry) -> Result<()>;
}

#[macro_export]
macro_rules! cultmesh_documents {
    ($name:ident { $($entry:ty => $schema_version:expr),* $(,)? }) => {
        #[derive(Clone, Copy, Debug, Default)]
        pub struct $name;

        impl $crate::CultMeshDocumentSet for $name {
            fn register_cache(
                &self,
                cache: &mut cultcache_rs::CultCache,
            ) -> anyhow::Result<()> {
                $(
                    cache.register_entry_type::<$entry>()?;
                )*
                Ok(())
            }

            fn register_documents(
                &self,
                registry: &mut cultnet_rs::CultNetDocumentRegistry,
            ) -> anyhow::Result<()> {
                $(
                    registry.register(cultnet_rs::CultNetDocumentBinding::for_entry::<$entry>(
                        Some($schema_version.to_string()),
                    ));
                )*
                Ok(())
            }
        }
    };
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CultMeshNodeOptions {
    pub runtime_id: String,
    pub pull_on_start: bool,
}

impl Default for CultMeshNodeOptions {
    fn default() -> Self {
        Self {
            runtime_id: "cultmesh-local".to_string(),
            pull_on_start: true,
        }
    }
}

pub struct CultMeshNode {
    runtime_id: String,
    cache: CultCache,
    documents: CultNetDocumentRegistry,
}

impl CultMeshNode {
    pub fn runtime_id(&self) -> &str {
        &self.runtime_id
    }

    pub fn cache(&self) -> &CultCache {
        &self.cache
    }

    pub fn cache_mut(&mut self) -> &mut CultCache {
        &mut self.cache
    }

    pub fn documents(&self) -> &CultNetDocumentRegistry {
        &self.documents
    }

    pub fn get<T: DatabaseEntry>(&self, key: &str) -> Result<Option<T>> {
        self.cache.get::<T>(key)
    }

    pub fn get_required<T: DatabaseEntry>(&self, key: &str) -> Result<T> {
        self.cache.get_required::<T>(key)
    }

    pub fn put<T: DatabaseEntry>(&mut self, key: impl Into<String>, value: &T) -> Result<T> {
        self.cache.put(key, value)
    }

    pub fn delete<T: DatabaseEntry>(&mut self, key: &str) -> Result<bool> {
        self.cache.delete::<T>(key)
    }

    pub fn flush(&mut self) -> Result<()> {
        let snapshot = self.cache.snapshot();
        for envelope in snapshot {
            match envelope.r#type.as_str() {
                "" => return Err(anyhow!("CultMesh cannot flush an envelope with empty type")),
                _ => {}
            }
        }
        Ok(())
    }
}

pub struct CultMesh;

impl CultMesh {
    pub fn create_node<D>(
        cache_path: impl AsRef<Path>,
        documents: D,
        options: CultMeshNodeOptions,
    ) -> Result<CultMeshNode>
    where
        D: CultMeshDocumentSet,
    {
        if options.runtime_id.trim().is_empty() {
            return Err(anyhow!("CultMesh runtime_id must be non-empty"));
        }

        let cache_path = cache_path.as_ref();
        if cache_path.as_os_str().is_empty() {
            return Err(anyhow!("CultMesh cache_path must be non-empty"));
        }

        let mut cache = CultCache::new();
        documents.register_cache(&mut cache)?;
        cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(cache_path));
        if options.pull_on_start {
            cache.pull_all_backing_stores()
                .with_context(|| format!("failed to load CultMesh node {}", cache_path.display()))?;
        }

        let mut registry = CultNetDocumentRegistry::new();
        documents.register_documents(&mut registry)?;

        Ok(CultMeshNode {
            runtime_id: options.runtime_id,
            cache,
            documents: registry,
        })
    }

    pub fn start_node<D>(cache_path: impl AsRef<Path>, documents: D) -> Result<CultMeshNode>
    where
        D: CultMeshDocumentSet,
    {
        Self::create_node(cache_path, documents, CultMeshNodeOptions::default())
    }
}

pub fn create_node<D>(
    cache_path: impl AsRef<Path>,
    documents: D,
    options: CultMeshNodeOptions,
) -> Result<CultMeshNode>
where
    D: CultMeshDocumentSet,
{
    CultMesh::create_node(cache_path, documents, options)
}

pub fn start_node<D>(cache_path: impl AsRef<Path>, documents: D) -> Result<CultMeshNode>
where
    D: CultMeshDocumentSet,
{
    CultMesh::start_node(cache_path, documents)
}

#[cfg(test)]
mod tests {
    use super::*;
    use cultcache_rs::DatabaseEntry;
    use pretty_assertions::assert_eq;

    #[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
    #[cultcache(type = "cultmesh.note", schema = "CultMeshNote")]
    struct MeshNote {
        #[cultcache(key = 0)]
        title: String,
        #[cultcache(key = 1)]
        body: String,
    }

    cultmesh_documents!(TestDocuments {
        MeshNote => "cultmesh.note.v0",
    });

    #[test]
    fn durable_node_round_trips_typed_documents() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let store = temp.path().join("world.ccmp");

        let mut node = CultMesh::create_node(
            &store,
            TestDocuments,
            CultMeshNodeOptions {
                runtime_id: "test-node".to_string(),
                ..CultMeshNodeOptions::default()
            },
        )?;
        assert_eq!(node.runtime_id(), "test-node");
        node.put(
            "note:intro",
            &MeshNote {
                title: "hello".to_string(),
                body: "from CultMesh".to_string(),
            },
        )?;
        node.flush()?;

        let reopened = CultMesh::start_node(&store, TestDocuments)?;
        assert_eq!(
            reopened.get_required::<MeshNote>("note:intro")?,
            MeshNote {
                title: "hello".to_string(),
                body: "from CultMesh".to_string(),
            }
        );
        Ok(())
    }

    #[test]
    fn node_refuses_empty_runtime_identity() {
        let temp = tempfile::tempdir().expect("tempdir");
        let error = match CultMesh::create_node(
            temp.path().join("world.ccmp"),
            TestDocuments,
            CultMeshNodeOptions {
                runtime_id: " ".to_string(),
                ..CultMeshNodeOptions::default()
            },
        ) {
            Ok(_) => panic!("empty runtime identity should fail"),
            Err(error) => error,
        };
        assert!(error.to_string().contains("runtime_id"));
    }
}
