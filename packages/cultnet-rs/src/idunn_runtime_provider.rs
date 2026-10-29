//! The provider half of `gamecult.runtime_presence_health.v2` for Rust runtimes.
//!
//! An Idunn-managed service attests its own health: it opens the root-provisioned
//! runtime bundle with the two signing credentials Idunn hands it, reports its
//! health as it changes, and signs presence records that Odin can authenticate
//! and Idunn's route challenge can read. This module owns that provider half and
//! owns no network; publishing the signed records is a separate concern that
//! consumes [`IdunnRuntimePresenceSigner::sign`].
//!
//! The module compiles and runs on every platform an Idunn driver launches:
//! Idunn's systemd driver (unix) delivers the credentials as inherited
//! descriptors under root custody, its host driver (Windows) as files in the
//! user's profile. Only the descriptor delivery, root custody and the write-lease
//! lock are unix-only.
//!
//! ```no_run
//! use cultnet_rs::{
//!     GameCultRuntimeCapability, IdunnRuntimeAuthority, IdunnRuntimeCredentials,
//!     IdunnRuntimePresenceSigner, RuntimePresenceState,
//! };
//!
//! # fn main() -> anyhow::Result<()> {
//! // The host driver's delivery. Under Idunn's systemd driver (unix) use
//! // `IdunnRuntimeCredentials::from_systemd_descriptors()` instead.
//! let credentials = IdunnRuntimeCredentials::from_host_environment()?;
//! // The runtime bundle, the candidate bind and the write lease are read from the
//! // launch environment Idunn set.
//! let authority = IdunnRuntimeAuthority::open(
//!     "my-service",
//!     "my-service.runtime-health.v1",
//!     credentials,
//! )?;
//! authority.require_candidate_bind("127.0.0.1:14103")?;
//! let mut signer = IdunnRuntimePresenceSigner::new(
//!     authority,
//!     vec![GameCultRuntimeCapability {
//!         capability: "my.capability".into(),
//!         schema: "my.schema.v1".into(),
//!         compatibility: "v1".into(),
//!         capacity: 1,
//!     }],
//! );
//! let warming = signer.sign("starting")?; // the signer starts Warming
//! signer.report_health(RuntimePresenceState::Active);
//! let active = signer.sign("ready")?;
//! # let _ = (warming, active);
//! # Ok(())
//! # }
//! ```

use std::fs::File;
#[cfg(unix)]
use std::fs::OpenOptions;
use std::io::Read;
#[cfg(unix)]
use std::os::fd::{AsRawFd, FromRawFd, RawFd};
#[cfg(unix)]
use std::os::unix::fs::MetadataExt;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result, anyhow, bail, ensure};
use cultcache_rs::{CultCacheEnvelope, DatabaseEntry, SingleFileMessagePackBackingStore};
use zeroize::Zeroizing;

#[cfg(unix)]
use crate::IDUNN_RUNTIME_ACTIVATION_CREDENTIAL_NAME;
#[cfg(unix)]
use crate::RUNTIME_PRESENCE_IDENTITY_FD_NAME;
use crate::{
    CultNetMessage, CultNetRawDocumentRecord, CultNetRawPayloadEncoding,
    GAMECULT_RUNTIME_PRESENCE_HEALTH_SCHEMA, GameCultProviderHealthIdentity,
    GameCultRuntimeCapability, GameCultRuntimePresenceHealthPurpose,
    GameCultRuntimePresenceHealthRecord, IDUNN_ACTIVATION_CREDENTIAL_ENVIRONMENT,
    IDUNN_EXPECTED_INCARNATION_SCHEMA, IDUNN_PROCESS_WRITE_LEASE_ENVIRONMENT,
    IDUNN_RUNTIME_ACTIVATION_SCHEMA, IDUNN_RUNTIME_BUNDLE_ENVIRONMENT,
    IDUNN_RUNTIME_CANDIDATE_BIND_ENVIRONMENT, IdunnExpectedIncarnationRecord,
    IdunnRuntimeActivationRecord, IdunnRuntimeActivationSigner, RUNTIME_PRESENCE_IDENTITY_BINDING,
    ServiceIdentitySigner, open_service_identity_credential_reader,
};
#[cfg(unix)]
use crate::{IDUNN_PROCESS_WRITE_LEASE_SCHEMA, IdunnProcessWriteLeaseRecord};

#[cfg(unix)]
const LISTEN_PID_ENVIRONMENT: &str = "LISTEN_PID";
#[cfg(unix)]
const LISTEN_FDS_ENVIRONMENT: &str = "LISTEN_FDS";
#[cfg(unix)]
const LISTEN_FDNAMES_ENVIRONMENT: &str = "LISTEN_FDNAMES";
/// `SD_LISTEN_FDS_START` in sd_listen_fds(3).
#[cfg(unix)]
const LISTEN_FDS_START: RawFd = 3;
const MAX_RECENT_WARMING_PROOFS: usize = 64;
const MAX_DETAIL_BYTES: usize = 512;
const DOCUMENT_ROLE: &str = "runtime-presence-health-publisher";
const DOCUMENT_TAGS: [&str; 2] = ["cultnet.transport.rudp.v0", "runtime-presence"];

// ------------------------------------------------------------------ host port

/// The process facts the provider reads: environment, pid and inherited
/// descriptors. Production reads the real process; tests supply a fake, so the
/// rules run without mutating the test process's own environment.
trait LaunchHost {
    fn var(&self, name: &str) -> Option<String>;
    fn remove_var(&mut self, name: &str);
    fn process_id(&self) -> u32;
    /// Takes sole ownership of an inherited descriptor, or refuses if it is not open.
    #[cfg(unix)]
    fn adopt_descriptor(&mut self, descriptor: RawFd) -> Result<File>;
}

struct ProcessHost;

impl LaunchHost for ProcessHost {
    fn var(&self, name: &str) -> Option<String> {
        std::env::var(name).ok()
    }

    fn remove_var(&mut self, name: &str) {
        // SAFETY: the credential helpers document that they run during start-up
        // before the service spawns threads, which is the contract
        // `std::env::remove_var` needs.
        unsafe { std::env::remove_var(name) }
    }

    fn process_id(&self) -> u32 {
        std::process::id()
    }

    #[cfg(unix)]
    fn adopt_descriptor(&mut self, descriptor: RawFd) -> Result<File> {
        // SAFETY: F_GETFD takes an integer descriptor and dereferences nothing.
        if unsafe { libc::fcntl(descriptor, libc::F_GETFD) } == -1 {
            return Err(std::io::Error::last_os_error())
                .with_context(|| format!("inherited descriptor {descriptor} is not open"));
        }
        // SAFETY: the caller proved the descriptor is open, names it exactly once
        // and has not adopted it before, so this is its only owner.
        Ok(unsafe { File::from_raw_fd(descriptor) })
    }
}

fn optional_environment(host: &impl LaunchHost, name: &str) -> Result<Option<String>> {
    match host.var(name) {
        None => Ok(None),
        Some(value) if !value.is_empty() && value.trim() == value => Ok(Some(value)),
        Some(_) => bail!("{name} is empty or has surrounding whitespace"),
    }
}

fn required_environment(host: &impl LaunchHost, name: &str) -> Result<String> {
    optional_environment(host, name)?
        .ok_or_else(|| anyhow!("{name} is required for an Idunn-managed runtime"))
}

// -------------------------------------------------------------------- custody

/// What a path is, read without following links. std counts symlinks and, on
/// Windows, junctions as links; Profile custody accepts only the real thing.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum FileKind {
    Regular,
    Directory,
    Symlink,
    Other,
}

impl FileKind {
    fn of(metadata: &std::fs::Metadata) -> Self {
        let file_type = metadata.file_type();
        if file_type.is_symlink() {
            Self::Symlink
        } else if file_type.is_dir() {
            Self::Directory
        } else if file_type.is_file() {
            Self::Regular
        } else {
            Self::Other
        }
    }

    fn noun(self) -> &'static str {
        match self {
            Self::Directory => "a directory",
            _ => "a regular file",
        }
    }
}

/// What a custody-checked path must be.
#[derive(Clone, Copy, Debug)]
enum Rule {
    /// A record or lock Idunn provisions read-only.
    ReadOnlyFile,
    /// The runtime bundle directory.
    ReadOnlyDirectory,
    /// The write-lease record and its lock.
    ControlledFile,
}

impl Rule {
    fn kind(self) -> FileKind {
        match self {
            Self::ReadOnlyFile | Self::ControlledFile => FileKind::Regular,
            Self::ReadOnlyDirectory => FileKind::Directory,
        }
    }

    #[cfg(unix)]
    fn root_description(self) -> &'static str {
        match self {
            Self::ReadOnlyFile => "a root-owned read-only regular file",
            Self::ReadOnlyDirectory => "a root-owned read-only directory",
            Self::ControlledFile => "a root-controlled regular file",
        }
    }
}

/// Profile custody trusts the user's profile and checks only that the path is the
/// real thing: no owner or ACL, because Idunn's host driver sets none.
fn profile_accepts(kind: FileKind, rule: Rule) -> bool {
    kind == rule.kind()
}

/// The unix facts a root-custody rule reads. Rules are pure predicates over
/// these, so the whole policy is testable without owning root.
#[cfg(unix)]
#[derive(Clone, Copy, Debug)]
struct Facts {
    kind: FileKind,
    uid: u32,
    gid: u32,
    mode: u32,
    nlink: u64,
    len: u64,
}

#[cfg(unix)]
impl Facts {
    fn of(metadata: &std::fs::Metadata) -> Self {
        Self {
            kind: FileKind::of(metadata),
            uid: metadata.uid(),
            gid: metadata.gid(),
            mode: metadata.mode() & 0o7777,
            nlink: metadata.nlink(),
            len: metadata.len(),
        }
    }

    fn of_path(path: &Path, label: &str) -> Result<Self> {
        let metadata =
            std::fs::symlink_metadata(path).with_context(|| format!("inspecting {label}"))?;
        Ok(Self::of(&metadata))
    }

    fn satisfies(&self, rule: Rule) -> bool {
        match rule {
            Rule::ReadOnlyFile => self.is_root_read_only_file(),
            Rule::ReadOnlyDirectory => self.is_root_read_only_directory(),
            Rule::ControlledFile => self.is_root_controlled_file(),
        }
    }

    /// Root-owned regular file nobody can write, not even root's group.
    fn is_root_read_only_file(&self) -> bool {
        self.kind == FileKind::Regular && self.uid == 0 && self.mode & 0o222 == 0
    }

    fn is_root_read_only_directory(&self) -> bool {
        self.kind == FileKind::Directory && self.uid == 0 && self.mode & 0o222 == 0
    }

    /// Root-owned regular file that group and world cannot write.
    fn is_root_controlled_file(&self) -> bool {
        self.kind == FileKind::Regular && self.uid == 0 && self.mode & 0o022 == 0
    }

    fn is_root_controlled_directory(&self) -> bool {
        self.kind == FileKind::Directory && self.uid == 0 && self.mode & 0o022 == 0
    }

    /// An inherited signer: root-owned, mode exactly 0400, one link, not empty.
    fn is_signer_descriptor(&self) -> bool {
        self.kind == FileKind::Regular
            && self.uid == 0
            && self.gid == 0
            && self.mode == 0o400
            && self.nlink == 1
            && self.len > 0
    }
}

/// How much of the filesystem the provider trusts, derived from how the
/// credentials were delivered and never chosen by a caller. Idunn's systemd
/// driver provisions root-owned material (`Root`); its host driver writes into the
/// user's profile (`Profile`).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Custody {
    #[cfg(unix)]
    Root,
    Profile,
}

impl Custody {
    fn require_file(self, path: &Path, label: &str, rule: Rule) -> Result<()> {
        ensure!(path.is_absolute(), "{label} path is not absolute");
        let metadata =
            std::fs::symlink_metadata(path).with_context(|| format!("inspecting {label}"))?;
        match self {
            Self::Profile => ensure!(
                profile_accepts(FileKind::of(&metadata), rule),
                "{label} is not {} (a link is refused)",
                rule.kind().noun()
            ),
            #[cfg(unix)]
            Self::Root => ensure!(
                Facts::of(&metadata).satisfies(rule),
                "{label} is not {}",
                rule.root_description()
            ),
        }
        Ok(())
    }

    /// The sibling `.lock` of a record. Only root custody inspects it.
    #[cfg(unix)]
    fn require_lock(self, path: &Path, label: &str, rule: Rule) -> Result<()> {
        match self {
            Self::Profile => Ok(()),
            Self::Root => self.require_file(path, label, rule),
        }
    }

    /// Every directory from `path` up to the filesystem root is root-controlled.
    /// Only root custody walks the chain.
    #[cfg(unix)]
    fn require_directory_chain(self, path: &Path, label: &str) -> Result<()> {
        if self == Self::Profile {
            return Ok(());
        }
        ensure!(path.is_absolute(), "{label} path is not absolute");
        for directory in path.ancestors() {
            let facts = Facts::of_path(directory, label)?;
            ensure!(
                facts.is_root_controlled_directory(),
                "{label} is not entirely root-controlled"
            );
        }
        Ok(())
    }

    fn require_bundle(self, bundle: &Path) -> Result<()> {
        self.require_file(bundle, "Idunn runtime bundle", Rule::ReadOnlyDirectory)?;
        #[cfg(unix)]
        {
            let parent = bundle.parent().context("runtime bundle has no parent")?;
            self.require_directory_chain(parent, "Idunn runtime bundle parent")?;
        }
        Ok(())
    }
}

#[cfg(unix)]
fn sibling_lock_path(path: &Path) -> Result<PathBuf> {
    let mut name = path
        .file_name()
        .context("CultCache authority path has no file name")?
        .to_os_string();
    name.push(".lock");
    Ok(path.with_file_name(name))
}

// ---------------------------------------------------------------- credentials

/// The two signing credentials Idunn issues a launch: the one-launch activation
/// seed and the stable provider identity. Idunn's systemd driver delivers them
/// as OpenFile descriptors and its host driver as files named in the
/// environment; `from_systemd_descriptors` (unix only) and
/// [`from_host_environment`](Self::from_host_environment) read each form.
///
/// The delivery decides how much of the filesystem the authority trusts: systemd
/// descriptors carry root custody, the host files carry profile custody. A
/// caller never chooses it.
///
/// The raw constructor is not public, so a caller cannot choose custody:
///
/// ```compile_fail,E0624
/// let _ = cultnet_rs::IdunnRuntimeCredentials::new(std::io::empty(), std::io::empty());
/// ```
pub struct IdunnRuntimeCredentials {
    activation: Box<dyn Read>,
    provider: Box<dyn Read>,
    custody: Custody,
}

impl IdunnRuntimeCredentials {
    /// Wraps the host driver's activation seed and provider identity credential
    /// (profile custody). Crate-private: custody follows delivery, so a caller
    /// never picks it and uses a `from_*` constructor.
    pub(crate) fn new(activation: impl Read + 'static, provider: impl Read + 'static) -> Self {
        Self::delivered(activation, provider, Custody::Profile)
    }

    fn delivered(
        activation: impl Read + 'static,
        provider: impl Read + 'static,
        custody: Custody,
    ) -> Self {
        Self {
            activation: Box::new(activation),
            provider: Box::new(provider),
            custody,
        }
    }

    /// Takes the credentials from systemd's inherited descriptors, following
    /// sd_listen_fds_with_names(3). `LISTEN_PID` must be this process (or, under
    /// `PrivatePIDs`, this process must be pid 1 while `LISTEN_PID` names a host
    /// pid above 1); the descriptors are found by name and extra descriptors are
    /// tolerated; each must be a root-owned 0400 single-link read-only regular
    /// file and is marked close-on-exec. `LISTEN_PID`, `LISTEN_FDS` and
    /// `LISTEN_FDNAMES` are removed from the environment so no child inherits
    /// them.
    ///
    /// Call this during start-up, before the service spawns threads: it edits the
    /// process environment and takes ownership of descriptors 3 and above.
    #[cfg(unix)]
    pub fn from_systemd_descriptors() -> Result<Self> {
        Self::from_systemd_descriptors_in(&mut ProcessHost)
    }

    #[cfg(unix)]
    fn from_systemd_descriptors_in(host: &mut impl LaunchHost) -> Result<Self> {
        let (activation, provider) = take_systemd_descriptors(host, Custody::Root)?;
        Ok(Self::delivered(activation, provider, Custody::Root))
    }

    /// Takes the credentials from the files Idunn's host driver names in
    /// [`IDUNN_ACTIVATION_CREDENTIAL_ENVIRONMENT`] and
    /// [`RUNTIME_PRESENCE_IDENTITY_BINDING`]. The provider identity file is opened
    /// first; a missing, unreadable or non-regular one refuses and leaves the
    /// activation credential in place. The activation credential is one-shot: it is read
    /// into memory, its handle closed and the file removed, so the seed lives only
    /// in this process afterwards. A refusal names the variable and never the path.
    pub fn from_host_environment() -> Result<Self> {
        Self::from_host_environment_in(&ProcessHost)
    }

    fn from_host_environment_in(host: &impl LaunchHost) -> Result<Self> {
        let activation_path = PathBuf::from(required_environment(
            host,
            IDUNN_ACTIVATION_CREDENTIAL_ENVIRONMENT,
        )?);
        let provider_path = PathBuf::from(required_environment(
            host,
            RUNTIME_PRESENCE_IDENTITY_BINDING,
        )?);
        ensure!(
            activation_path.is_absolute() && provider_path.is_absolute(),
            "host credential paths are not absolute"
        );
        let provider = File::open(&provider_path).with_context(|| {
            format!("{RUNTIME_PRESENCE_IDENTITY_BINDING}: opening provider identity credential")
        })?;
        ensure!(
            provider
                .metadata()
                .with_context(|| {
                    format!("{RUNTIME_PRESENCE_IDENTITY_BINDING}: inspecting provider identity")
                })?
                .is_file(),
            "{RUNTIME_PRESENCE_IDENTITY_BINDING}: provider identity credential is not a regular file"
        );
        let mut seed = Zeroizing::new(Vec::new());
        {
            let mut file = File::open(&activation_path).with_context(|| {
                format!("{IDUNN_ACTIVATION_CREDENTIAL_ENVIRONMENT}: opening activation credential")
            })?;
            file.read_to_end(&mut seed).with_context(|| {
                format!("{IDUNN_ACTIVATION_CREDENTIAL_ENVIRONMENT}: reading activation credential")
            })?;
        }
        std::fs::remove_file(&activation_path).with_context(|| {
            format!("{IDUNN_ACTIVATION_CREDENTIAL_ENVIRONMENT}: retiring activation credential")
        })?;
        Ok(Self::new(SeedReader { seed, position: 0 }, provider))
    }
}

/// Reads an in-memory seed and wipes it when dropped.
struct SeedReader {
    seed: Zeroizing<Vec<u8>>,
    position: usize,
}

impl Read for SeedReader {
    fn read(&mut self, buffer: &mut [u8]) -> std::io::Result<usize> {
        let remaining = &self.seed[self.position..];
        let count = remaining.len().min(buffer.len());
        buffer[..count].copy_from_slice(&remaining[..count]);
        self.position += count;
        Ok(count)
    }
}

/// systemd's `LISTEN_PID` is a host pid. `PrivatePIDs=yes` makes the main process
/// pid 1 in a private namespace, where it cannot see that host pid; a descendant
/// has a pid above 1 and must not reuse the inherited set.
#[cfg(unix)]
fn listen_pid_matches(listen_pid: &str, process_id: u32) -> bool {
    match listen_pid.parse::<u32>() {
        Ok(declared) if declared.to_string() == listen_pid => {
            declared == process_id || (process_id == 1 && declared > 1)
        }
        _ => false,
    }
}

#[cfg(unix)]
fn take_systemd_descriptors(host: &mut impl LaunchHost, custody: Custody) -> Result<(File, File)> {
    let listen_pid = required_environment(&*host, LISTEN_PID_ENVIRONMENT);
    let listen_fds = required_environment(&*host, LISTEN_FDS_ENVIRONMENT);
    let listen_fd_names = required_environment(&*host, LISTEN_FDNAMES_ENVIRONMENT);
    // Whatever the verdict, no child of this process inherits the claim.
    for name in [
        LISTEN_PID_ENVIRONMENT,
        LISTEN_FDS_ENVIRONMENT,
        LISTEN_FDNAMES_ENVIRONMENT,
    ] {
        host.remove_var(name);
    }
    let (listen_pid, listen_fds, listen_fd_names) = (listen_pid?, listen_fds?, listen_fd_names?);
    ensure!(
        listen_pid_matches(&listen_pid, host.process_id()),
        "systemd signer descriptors belong to a different process"
    );
    let count = listen_fds
        .parse::<usize>()
        .ok()
        .filter(|count| count.to_string() == listen_fds)
        .context("parsing systemd LISTEN_FDS")?;
    let names = listen_fd_names.split(':').collect::<Vec<_>>();
    ensure!(
        names.len() == count,
        "systemd LISTEN_FDNAMES does not name every inherited descriptor"
    );
    let activation = find_descriptor(&names, IDUNN_RUNTIME_ACTIVATION_CREDENTIAL_NAME)?;
    let provider = find_descriptor(&names, RUNTIME_PRESENCE_IDENTITY_FD_NAME)?;
    let activation = host.adopt_descriptor(activation)?;
    let activation = protect_descriptor(activation, "runtime activation signer", custody)?;
    let provider = host.adopt_descriptor(provider)?;
    let provider = protect_descriptor(provider, "stable provider signer", custody)?;
    Ok((activation, provider))
}

/// The descriptor number of the one descriptor called `name`.
#[cfg(unix)]
fn find_descriptor(names: &[&str], name: &str) -> Result<RawFd> {
    let mut matches = names
        .iter()
        .enumerate()
        .filter(|(_, candidate)| **candidate == name)
        .map(|(index, _)| LISTEN_FDS_START + index as RawFd);
    let descriptor = matches
        .next()
        .ok_or_else(|| anyhow!("systemd did not pass the {name} descriptor"))?;
    ensure!(
        matches.next().is_none(),
        "systemd passed the {name} descriptor more than once"
    );
    Ok(descriptor)
}

#[cfg(unix)]
fn protect_descriptor(file: File, label: &str, custody: Custody) -> Result<File> {
    // OpenFile descriptors arrive without close-on-exec; a child must never
    // inherit a signer, even on a path that refuses it below.
    // SAFETY: fcntl reads and sets flags of the descriptor `file` owns.
    let flags = unsafe { libc::fcntl(file.as_raw_fd(), libc::F_GETFD) };
    if flags == -1
        // SAFETY: as above.
        || unsafe { libc::fcntl(file.as_raw_fd(), libc::F_SETFD, flags | libc::FD_CLOEXEC) } == -1
    {
        return Err(std::io::Error::last_os_error())
            .with_context(|| format!("protecting {label} descriptor from child inheritance"));
    }
    // SAFETY: F_GETFL reads the flags of the descriptor `file` owns.
    let status = unsafe { libc::fcntl(file.as_raw_fd(), libc::F_GETFL) };
    if status == -1 {
        return Err(std::io::Error::last_os_error())
            .with_context(|| format!("reading {label} access mode"));
    }
    ensure!(
        status & libc::O_ACCMODE == libc::O_RDONLY,
        "{label} descriptor is not read-only"
    );
    if custody == Custody::Root {
        let facts = Facts::of(
            &file
                .metadata()
                .with_context(|| format!("inspecting {label}"))?,
        );
        ensure!(
            facts.is_signer_descriptor(),
            "{label} descriptor is not a root-owned 0400 singly-linked regular file"
        );
    }
    Ok(file)
}

// ------------------------------------------------------------------ authority

/// What the authority and every lease guard share: the verified records and the
/// custody policy their files are read under.
struct Records {
    expected: IdunnExpectedIncarnationRecord,
    expected_sha256: String,
    activation: IdunnRuntimeActivationRecord,
    activation_sha256: String,
    /// Set exactly when Expected requires a process write lease (unix only: no
    /// other platform can hold the lease lock, so `open` refuses the requirement).
    #[cfg(unix)]
    lease_path: Option<PathBuf>,
    #[cfg(unix)]
    custody: Custody,
}

/// One launch's verified Idunn authority: the Expected incarnation, the
/// activation witness bound to it, and the two signing keys Idunn issued.
///
/// Opening checks every binding the provider owes Idunn. The Expected target and
/// health contract equal the caller's; the activation binds Expected's canonical
/// digest and runtime id; the activation credential is the activation's signer;
/// the provider identity is the signer Expected selected; the write-lease
/// environment is present exactly when Expected requires a lease; and the
/// candidate-bind environment names Expected's route. The launch environment is
/// read here and nowhere else.
pub struct IdunnRuntimeAuthority {
    records: Arc<Records>,
    activation_signer: IdunnRuntimeActivationSigner,
    provider_signer: ServiceIdentitySigner<GameCultProviderHealthIdentity>,
    candidate_bind: Option<String>,
}

impl std::fmt::Debug for IdunnRuntimeAuthority {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("IdunnRuntimeAuthority")
            .field("target", &self.records.expected.target)
            .field("runtime_id", &self.records.expected.runtime_id)
            .finish_non_exhaustive()
    }
}

impl IdunnRuntimeAuthority {
    /// Opens the runtime bundle Idunn launched this process with, for the target
    /// `target` and the health contract `health_contract`, with the credentials
    /// Idunn issued. The bundle directory comes from
    /// [`IDUNN_RUNTIME_BUNDLE_ENVIRONMENT`], the candidate bind from
    /// [`IDUNN_RUNTIME_CANDIDATE_BIND_ENVIRONMENT`] and the write lease from
    /// [`IDUNN_PROCESS_WRITE_LEASE_ENVIRONMENT`]; no caller supplies a path.
    ///
    /// The credentials' delivery decides the custody the bundle is held to. Under
    /// systemd descriptors (root custody, unix) the bundle directory, its
    /// `expected.cc` and `activation.cc`, their `.lock` siblings and every parent
    /// directory must be root-owned and not writable by others, and none may be a
    /// symlink; the write lease, if Expected requires one, is held to the same
    /// custody. Under any other delivery (profile custody, every platform) the
    /// bundle directory and the two records must be a real directory and real
    /// regular files, never a link. A runtime that requires a
    /// write lease can only be opened on unix, where the lease lock exists.
    pub fn open(
        target: &str,
        health_contract: &str,
        credentials: IdunnRuntimeCredentials,
    ) -> Result<Self> {
        Self::open_in(&ProcessHost, target, health_contract, credentials)
    }

    fn open_in(
        host: &impl LaunchHost,
        target: &str,
        health_contract: &str,
        credentials: IdunnRuntimeCredentials,
    ) -> Result<Self> {
        let custody = credentials.custody;
        let bundle = PathBuf::from(required_environment(
            host,
            IDUNN_RUNTIME_BUNDLE_ENVIRONMENT,
        )?);
        ensure!(
            bundle.is_absolute(),
            "{IDUNN_RUNTIME_BUNDLE_ENVIRONMENT} is not an absolute path"
        );
        custody.require_bundle(&bundle)?;
        let (key, payload) = read_authority_record(
            &bundle.join("expected.cc"),
            IdunnExpectedIncarnationRecord::TYPE,
            IDUNN_EXPECTED_INCARNATION_SCHEMA,
            custody,
        )?;
        let expected = IdunnExpectedIncarnationRecord::decode_canonical(&payload)?;
        ensure!(
            key == expected.target,
            "Expected record key is not its target"
        );
        ensure!(
            expected.target == target && expected.health_contract == health_contract,
            "Idunn Expected target or health contract does not match this runtime"
        );
        let (key, payload) = read_authority_record(
            &bundle.join("activation.cc"),
            IdunnRuntimeActivationRecord::TYPE,
            IDUNN_RUNTIME_ACTIVATION_SCHEMA,
            custody,
        )?;
        let activation = IdunnRuntimeActivationRecord::decode_canonical(&payload)?;
        ensure!(
            key == expected.target,
            "activation record key is not the Expected target"
        );
        let expected_sha256 = expected.canonical_sha256()?;
        ensure!(
            activation.expected_projection_sha256 == expected_sha256
                && activation.runtime_id == expected.runtime_id,
            "runtime activation does not bind the bundled Expected projection"
        );

        // The lease lock exists only on unix; a requirement no process here can
        // hold is refused rather than signed around.
        #[cfg(not(unix))]
        ensure!(
            !expected.write_lease_required,
            "write_lease_required: Expected requires a process write lease, which this platform cannot hold"
        );
        let lease_path = optional_environment(host, IDUNN_PROCESS_WRITE_LEASE_ENVIRONMENT)?;
        ensure!(
            lease_path.is_some() == expected.write_lease_required,
            "Idunn process write-lease environment does not match Expected"
        );
        #[cfg(unix)]
        let lease_path = lease_path.map(PathBuf::from);
        #[cfg(unix)]
        if let Some(path) = &lease_path {
            ensure!(
                path.is_absolute(),
                "Idunn process write-lease path is not absolute"
            );
        }
        let candidate_bind = optional_environment(host, IDUNN_RUNTIME_CANDIDATE_BIND_ENVIRONMENT)?;
        match (&expected.route, &candidate_bind) {
            (Some(route), Some(bind)) => ensure!(
                route.candidate_endpoint == format!("{}://{bind}", route.transport),
                "Idunn candidate bind differs from the Expected route"
            ),
            (None, None) => {}
            _ => bail!("Idunn candidate bind environment does not match Expected's route"),
        }

        let IdunnRuntimeCredentials {
            activation: activation_credential,
            provider: provider_credential,
            ..
        } = credentials;
        let activation_signer =
            IdunnRuntimeActivationSigner::from_credential_reader(activation_credential)?;
        ensure!(
            activation_signer.identity_id() == activation.activation_signer_identity_id
                && activation_signer.public_key() == activation.activation_signer_public_key,
            "runtime activation credential does not belong to the Idunn activation record"
        );
        // The shared reader owns profile validation, platform binding, seed
        // unprotection and domain-separated signing; this module never parses or
        // exports seed bytes.
        let provider_signer = open_service_identity_credential_reader::<
            GameCultProviderHealthIdentity,
        >(provider_credential)?;
        ensure!(
            provider_signer.entry().identity_id == expected.expected_signer_identity_id,
            "provider signer is not the identity selected by Expected"
        );

        Ok(Self {
            records: Arc::new(Records {
                activation_sha256: activation.canonical_sha256()?,
                expected_sha256,
                expected,
                activation,
                #[cfg(unix)]
                lease_path,
                #[cfg(unix)]
                custody,
            }),
            activation_signer,
            provider_signer,
            candidate_bind,
        })
    }

    /// Idunn's admitted description of this incarnation.
    pub fn expected(&self) -> &IdunnExpectedIncarnationRecord {
        &self.records.expected
    }

    /// The activation witness Idunn issued for this launch.
    pub fn activation(&self) -> &IdunnRuntimeActivationRecord {
        &self.records.activation
    }

    /// The endpoint every presence states as bound: Expected's candidate route,
    /// confirmed at open against Idunn's candidate-bind environment. `None` when
    /// the incarnation has no route.
    pub fn bound_endpoint(&self) -> Option<&str> {
        self.records
            .expected
            .route
            .as_ref()
            .map(|route| route.candidate_endpoint.as_str())
    }

    /// Checks that `bind` (a `host:port`) is the address Idunn will challenge:
    /// the candidate bind Idunn put in [`IDUNN_RUNTIME_CANDIDATE_BIND_ENVIRONMENT`],
    /// which open already tied to Expected's route. Call it with the address the
    /// service actually binds, before it listens.
    pub fn require_candidate_bind(&self, bind: &str) -> Result<()> {
        let route = self
            .records
            .expected
            .route
            .as_ref()
            .context("Expected has no admitted route")?;
        ensure!(
            self.candidate_bind.as_deref() == Some(bind)
                && route.candidate_endpoint == format!("{}://{bind}", route.transport),
            "candidate bind differs from Idunn's candidate bind and the Expected route"
        );
        Ok(())
    }
}

fn read_authority_record(
    path: &Path,
    record_type: &str,
    schema: &str,
    custody: Custody,
) -> Result<(String, Vec<u8>)> {
    custody.require_file(path, "runtime authority record", Rule::ReadOnlyFile)?;
    #[cfg(unix)]
    custody.require_lock(
        &sibling_lock_path(path)?,
        "runtime authority lock",
        Rule::ReadOnlyFile,
    )?;
    read_snapshot(path, "runtime authority record", |entries| {
        let [envelope] = entries.as_slice() else {
            bail!("runtime authority store must contain exactly one record");
        };
        ensure!(
            envelope.r#type == record_type && envelope.schema_id.as_deref() == Some(schema),
            "runtime authority store has the wrong typed envelope"
        );
        Ok((envelope.key.clone(), envelope.payload.clone()))
    })
}

/// Reads `path` as a CultCache store under its shared lock and hands the
/// envelopes to `read`. CultCache's own errors carry the store's path, which
/// under the host driver lies in the user's profile, so a failure of the store
/// itself is reported by `label` and a path-free kind alone
/// ([`store_failure_kind`]); `read`'s refusals pass through.
fn read_snapshot<T>(
    path: &Path,
    label: &str,
    read: impl FnOnce(Vec<CultCacheEnvelope>) -> Result<T>,
) -> Result<T> {
    let mut outcome = None;
    let store =
        SingleFileMessagePackBackingStore::new(path).with_read_only_shared_snapshot(|entries| {
            outcome = Some(read(entries));
            Ok(())
        });
    match (store, outcome) {
        (Ok(()), Some(outcome)) => outcome,
        (Err(error), _) => bail!(
            "{label} could not be read as a CultCache store: {}",
            store_failure_kind(&error)
        ),
        (Ok(()), None) => bail!("{label} could not be read as a CultCache store"),
    }
}

/// What kept a store from being read, as a label that names no path: CultCache's
/// verdict on the bytes, else the kind of the I/O failure in the chain.
fn store_failure_kind(error: &anyhow::Error) -> &'static str {
    use cultcache_rs::{CultCacheStoreUnreadable, CultCacheStoreUnreadableKind};
    if let Some(unreadable) = error.downcast_ref::<CultCacheStoreUnreadable>() {
        return match unreadable.kind {
            CultCacheStoreUnreadableKind::Undecodable => "not decodable",
            CultCacheStoreUnreadableKind::UnsupportedFormat => {
                "in a format this runtime does not read"
            }
            _ => "unreadable",
        };
    }
    match error
        .chain()
        .find_map(|cause| cause.downcast_ref::<std::io::Error>())
        .map(std::io::Error::kind)
    {
        Some(std::io::ErrorKind::NotFound) => "not found",
        Some(std::io::ErrorKind::PermissionDenied) => "permission denied",
        Some(_) => "I/O error",
        None => "unreadable",
    }
}

// ---------------------------------------------------------------- write lease

/// Proof that this process holds Idunn's process write lease. It holds the
/// lease's sibling `.lock` shared for as long as any clone lives, which fences
/// Idunn's lease replacement and every other writer that takes the lock
/// exclusively. Gate state writes on [`require_current`](Self::require_current).
///
/// The lease lock exists only on unix. The type exists everywhere so consumer code
/// needs no `cfg`, but on other platforms no guard can be constructed: `open`
/// refuses a runtime that requires a lease.
#[derive(Clone)]
pub struct ProcessWriteLeaseGuard {
    held: Arc<HeldLease>,
}

#[cfg(unix)]
struct HeldLease {
    _lock: File,
    records: Arc<Records>,
    warming_proofs: Vec<String>,
    lease_sha256: String,
}

/// No platform without the lease lock can hold a lease.
#[cfg(not(unix))]
enum HeldLease {}

impl HeldLease {
    #[cfg(unix)]
    fn lease_sha256(&self) -> &str {
        &self.lease_sha256
    }

    #[cfg(not(unix))]
    fn lease_sha256(&self) -> &str {
        match *self {}
    }

    #[cfg(unix)]
    fn require_current(&self) -> Result<()> {
        let observed = load_lease(&self.records, &self.warming_proofs)?;
        ensure!(
            observed.as_deref() == Some(self.lease_sha256()),
            "process-write lease is no longer the admitted lifetime grant"
        );
        Ok(())
    }

    #[cfg(not(unix))]
    fn require_current(&self) -> Result<()> {
        match *self {}
    }
}

impl std::fmt::Debug for ProcessWriteLeaseGuard {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("ProcessWriteLeaseGuard")
            .field("lease_sha256", &self.lease_sha256())
            .finish_non_exhaustive()
    }
}

impl ProcessWriteLeaseGuard {
    /// The canonical digest of the lease record this guard was admitted under.
    pub fn lease_sha256(&self) -> &str {
        self.held.lease_sha256()
    }

    /// Fails unless the lease record on disk is still the admitted lifetime grant.
    pub fn require_current(&self) -> Result<()> {
        self.held.require_current()
    }
}

/// The digest of the lease on disk if it exists and binds this incarnation and
/// one of `warming_proofs`; `None` while the record is absent or empty.
#[cfg(unix)]
fn load_lease(records: &Records, warming_proofs: &[String]) -> Result<Option<String>> {
    let Some(path) = present_lease_path(records)? else {
        return Ok(None);
    };
    read_snapshot(path, "process-write-lease record", |entries| {
        let [envelope] = entries.as_slice() else {
            if entries.is_empty() {
                return Ok(None);
            }
            bail!("process-write-lease store is ambiguous");
        };
        ensure!(
            envelope.r#type == IdunnProcessWriteLeaseRecord::TYPE
                && envelope.schema_id.as_deref() == Some(IDUNN_PROCESS_WRITE_LEASE_SCHEMA),
            "process-write-lease store has the wrong typed envelope"
        );
        let lease = IdunnProcessWriteLeaseRecord::decode_canonical(&envelope.payload)?;
        ensure!(
            envelope.key == lease.target,
            "process-write-lease key is not its target"
        );
        exact_lease_sha256(&lease, records, warming_proofs).map(Some)
    })
}

/// The lease record's path once the record exists and its files pass custody;
/// `None` while Idunn has not written it.
#[cfg(unix)]
fn present_lease_path(records: &Records) -> Result<Option<&Path>> {
    let path = records
        .lease_path
        .as_deref()
        .context("Expected does not require a process write lease")?;
    match std::fs::symlink_metadata(path) {
        Ok(_) => {}
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(error.into()),
    }
    require_lease_custody(records.custody, path)?;
    Ok(Some(path))
}

#[cfg(unix)]
fn require_lease_custody(custody: Custody, path: &Path) -> Result<()> {
    custody.require_file(path, "process-write-lease record", Rule::ControlledFile)?;
    custody.require_lock(
        &sibling_lock_path(path)?,
        "process-write-lease lock",
        Rule::ControlledFile,
    )?;
    custody.require_directory_chain(
        path.parent().context("process-write lease has no parent")?,
        "process-write-lease parent",
    )
}

#[cfg(unix)]
fn exact_lease_sha256(
    lease: &IdunnProcessWriteLeaseRecord,
    records: &Records,
    warming_proofs: &[String],
) -> Result<String> {
    lease.validate()?;
    let expected = &records.expected;
    ensure!(
        lease.target == expected.target
            && lease.expected_projection_sha256 == records.expected_sha256
            && lease.plan_id == expected.plan_id
            && lease.incarnation_id == expected.incarnation_id
            && lease.sealed_release_id == expected.sealed_release_id
            && lease.activation_witness_sha256 == records.activation_sha256
            && Some(&lease.state_schema_generation) == expected.state_schema_generation.as_ref()
            && Some(&lease.state_contract_sha256) == expected.state_contract_sha256.as_ref()
            && lease.runtime_id == expected.runtime_id
            && lease.runtime_instance_id == records.activation.runtime_instance_id
            && warming_proofs
                .iter()
                .any(|proof| proof == &lease.warming_presence_sha256),
        "process-write lease does not bind a provider-owned Warming proof for this incarnation"
    );
    lease.canonical_sha256()
}

/// Takes the lease's shared lock and admits the record under it. `None` while
/// the record is absent.
#[cfg(unix)]
fn acquire_lease(
    records: &Arc<Records>,
    warming_proofs: &[String],
) -> Result<Option<ProcessWriteLeaseGuard>> {
    let Some(path) = present_lease_path(records)? else {
        return Ok(None);
    };
    let lock_path = sibling_lock_path(path)?;
    let lock = OpenOptions::new()
        .read(true)
        .open(&lock_path)
        .context("opening process-write-lease lock")?;
    fs2::FileExt::lock_shared(&lock).context("locking process-write-lease lock")?;
    // The first shared lock stays held while the snapshot is decoded; the
    // backing-store read takes its own nested shared lock so the canonical
    // CultCache decoder is the one reading the record.
    let Some(lease_sha256) = load_lease(records, warming_proofs)? else {
        return Ok(None);
    };
    Ok(Some(ProcessWriteLeaseGuard {
        held: Arc::new(HeldLease {
            _lock: lock,
            records: Arc::clone(records),
            warming_proofs: warming_proofs.to_vec(),
            lease_sha256,
        }),
    }))
}

/// `open` refuses a lease requirement on this platform, so nothing reaches here.
#[cfg(not(unix))]
fn acquire_lease(
    _records: &Arc<Records>,
    _warming_proofs: &[String],
) -> Result<Option<ProcessWriteLeaseGuard>> {
    bail!("write_lease_required: this platform cannot hold a process write lease")
}

// --------------------------------------------------------------------- signer

/// The health an app reports about itself. Every presence the signer signs
/// states the last one reported.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RuntimePresenceState {
    /// Starting, or waiting for Idunn's write lease. The signer begins here.
    Warming,
    /// Serving.
    Active,
    /// Serving with reduced ability.
    Degraded,
    /// Not able to serve.
    Failed,
}

impl RuntimePresenceState {
    /// The state string the presence record carries.
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Warming => "warming",
            Self::Active => "active",
            Self::Degraded => "degraded",
            Self::Failed => "failed",
        }
    }
}

/// One signed presence: the validated record, the raw CultNet document that
/// carries it, and the record's canonical digest (what a write lease names when
/// the presence is Warming).
#[derive(Clone, Debug)]
pub struct SignedRuntimePresence {
    pub record: GameCultRuntimePresenceHealthRecord,
    pub document: CultNetRawDocumentRecord,
    pub canonical_sha256: String,
}

/// Signs this launch's runtime presence and answers Idunn's route challenge from
/// local authority, with no network.
///
/// The signer is the one owner of reported health: it starts Warming, the app
/// moves it with [`report_health`](Self::report_health), and every presence it
/// signs, however it is carried, states that health. It takes the authority by
/// value, so one signer per authority is a fact of the types: a second signer
/// would restart the publisher sequence under the same runtime instance.
pub struct IdunnRuntimePresenceSigner {
    authority: IdunnRuntimeAuthority,
    capabilities: Vec<GameCultRuntimeCapability>,
    health: RuntimePresenceState,
    sequence: u64,
    warming_proofs: Vec<String>,
    lease: Option<ProcessWriteLeaseGuard>,
}

impl IdunnRuntimePresenceSigner {
    /// A Warming signer over `authority`, stating `capabilities` in every
    /// presence. Capabilities are validated when a presence is signed.
    pub fn new(
        authority: IdunnRuntimeAuthority,
        capabilities: Vec<GameCultRuntimeCapability>,
    ) -> Self {
        Self {
            authority,
            capabilities,
            health: RuntimePresenceState::Warming,
            sequence: 0,
            warming_proofs: Vec::new(),
            lease: None,
        }
    }

    /// The authority this signer owns.
    pub fn authority(&self) -> &IdunnRuntimeAuthority {
        &self.authority
    }

    /// Records the app's health. Until the first call the signer reports Warming.
    pub fn report_health(&mut self, state: RuntimePresenceState) {
        self.health = state;
    }

    /// The health the next presence will state.
    pub fn health(&self) -> RuntimePresenceState {
        self.health
    }

    /// Signs one presence now, in the reported health. Every call takes the next
    /// publisher sequence. A Warming presence never carries a lease and becomes a
    /// valid write-lease target; Active on a lease-required Expected is refused
    /// unless the signer holds a current [`ProcessWriteLeaseGuard`]; a held guard
    /// that is no longer current refuses Active and Degraded, while Failed still
    /// signs, without a lease digest, so a runtime that lost its lease can say so.
    pub fn sign(&mut self, detail: &str) -> Result<SignedRuntimePresence> {
        self.sign_at(detail, unix_millis()?)
    }

    /// [`sign`](Self::sign) with an explicit observation time.
    pub fn sign_at(
        &mut self,
        detail: &str,
        observed_at_unix_millis: u64,
    ) -> Result<SignedRuntimePresence> {
        let records = Arc::clone(&self.authority.records);
        let state = self.health;
        let write_lease_sha256 = match (state, &self.lease) {
            (RuntimePresenceState::Warming, _) | (RuntimePresenceState::Failed, None) => None,
            // A runtime that lost its lease must still be able to say it
            // failed; the correlation admits a not-ready presence without a
            // lease digest.
            (RuntimePresenceState::Failed, Some(guard)) => guard
                .require_current()
                .ok()
                .map(|()| guard.lease_sha256().to_owned()),
            (_, Some(guard)) => {
                guard.require_current()?;
                Some(guard.lease_sha256().to_owned())
            }
            (_, None) if records.expected.write_lease_required => {
                bail!(
                    "{} stateful presence lacks its process-write lease",
                    state.as_str()
                )
            }
            (_, None) => None,
        };
        self.sequence = self
            .sequence
            .checked_add(1)
            .ok_or_else(|| anyhow!("runtime presence publisher sequence overflow"))?;
        let expected = &records.expected;
        let mut record = GameCultRuntimePresenceHealthRecord {
            schema_version: GAMECULT_RUNTIME_PRESENCE_HEALTH_SCHEMA.into(),
            target: expected.target.clone(),
            expected_projection_sha256: records.expected_sha256.clone(),
            plan_id: expected.plan_id.clone(),
            incarnation_id: expected.incarnation_id.clone(),
            sealed_release_id: expected.sealed_release_id.clone(),
            activation_witness_sha256: records.activation_sha256.clone(),
            state_schema_generation: expected.state_schema_generation.clone(),
            state_contract_sha256: expected.state_contract_sha256.clone(),
            runtime_id: expected.runtime_id.clone(),
            runtime_instance_id: records.activation.runtime_instance_id.clone(),
            bound_endpoint: self.authority.bound_endpoint().map(str::to_owned),
            capabilities: self.capabilities.clone(),
            health_contract: expected.health_contract.clone(),
            state: state.as_str().into(),
            detail: truncate_utf8(detail, MAX_DETAIL_BYTES).into(),
            write_lease_sha256,
            signer_identity_id: self.authority.provider_signer.entry().identity_id.clone(),
            publisher_sequence: self.sequence,
            observed_at_unix_millis,
            signature_algorithm: "ed25519".into(),
            signature: Vec::new(),
            activation_signer_identity_id: records.activation.activation_signer_identity_id.clone(),
            activation_signature: Vec::new(),
        };
        let proof_payload = record.canonical_proof_payload()?;
        let provider_proof = self
            .authority
            .provider_signer
            .sign::<GameCultRuntimePresenceHealthPurpose>(&proof_payload);
        ensure!(
            provider_proof.identity_id == record.signer_identity_id,
            "provider signer identity changed after runtime admission"
        );
        record.signature = provider_proof.signature;
        record.activation_signature = self
            .authority
            .activation_signer
            .sign_presence_proof(&record)?;
        record.validate()?;
        let payload = rmp_serde::to_vec(&record)?;
        ensure!(
            rmp_serde::from_slice::<GameCultRuntimePresenceHealthRecord>(&payload)? == record,
            "runtime presence did not round-trip canonically"
        );
        let canonical_sha256 = record.canonical_sha256()?;
        let document = CultNetRawDocumentRecord {
            schema_id: GAMECULT_RUNTIME_PRESENCE_HEALTH_SCHEMA.into(),
            record_key: expected.target.clone(),
            stored_at: chrono::DateTime::from_timestamp_millis(i64::try_from(
                observed_at_unix_millis,
            )?)
            .context("runtime presence observation time is invalid")?
            .to_rfc3339(),
            payload_encoding: CultNetRawPayloadEncoding::Messagepack,
            payload,
            source_runtime_id: Some(expected.runtime_id.clone()),
            source_agent_id: Some(record.signer_identity_id.clone()),
            source_role: Some(DOCUMENT_ROLE.into()),
            tags: Some(DOCUMENT_TAGS.iter().map(|tag| (*tag).into()).collect()),
        };
        if state == RuntimePresenceState::Warming {
            self.warming_proofs.push(canonical_sha256.clone());
            if self.warming_proofs.len() > MAX_RECENT_WARMING_PROOFS {
                self.warming_proofs.remove(0);
            }
        }
        Ok(SignedRuntimePresence {
            record,
            document,
            canonical_sha256,
        })
    }

    /// Looks for Idunn's process write lease. `Ok(None)` while Idunn has not
    /// written it; an error when a record exists but does not bind this
    /// incarnation and a Warming presence this signer signed within its last 64.
    /// The guard it returns is kept: later non-Warming presences carry its lease
    /// digest. The caller waits, sleeping between calls and signing Warming
    /// heartbeats, as its own loop decides.
    pub fn try_acquire_write_lease(&mut self) -> Result<Option<ProcessWriteLeaseGuard>> {
        ensure!(
            self.authority.records.expected.write_lease_required,
            "Expected does not require a process write lease"
        );
        if let Some(guard) = &self.lease {
            guard.require_current()?;
            return Ok(Some(guard.clone()));
        }
        let guard = acquire_lease(&self.authority.records, &self.warming_proofs)?;
        self.lease = guard.clone();
        Ok(guard)
    }

    /// Answers Idunn's route challenge: a `SnapshotResponseRaw` carrying a fresh
    /// presence in the reported health. Only a plain snapshot request for this
    /// runtime's presence schema and target is answered; another schema, another
    /// record key, a shard-addressed request, a request of any other kind or an
    /// empty or untrimmed message id is refused.
    pub fn answer_route_observation(&mut self, request: &CultNetMessage) -> Result<CultNetMessage> {
        let CultNetMessage::SnapshotRequest {
            message_id,
            schema_ids,
            record_keys,
        } = request
        else {
            bail!("Idunn route observation is not a plain snapshot request");
        };
        ensure!(
            !message_id.is_empty() && message_id.trim() == message_id,
            "Idunn route observation requires a message id"
        );
        let target = &self.authority.records.expected.target;
        ensure!(
            schema_ids.as_ref().is_none_or(|schemas| schemas
                .iter()
                .any(|schema| schema == GAMECULT_RUNTIME_PRESENCE_HEALTH_SCHEMA))
                && record_keys
                    .as_ref()
                    .is_none_or(|keys| keys.iter().any(|key| key == target)),
            "Idunn route observation does not request this runtime presence document"
        );
        let signed = self.sign(&format!("route-observation:{message_id}"))?;
        Ok(CultNetMessage::SnapshotResponseRaw {
            message_id: message_id.clone(),
            documents: vec![signed.document],
        })
    }
}

fn truncate_utf8(text: &str, max_bytes: usize) -> &str {
    if text.len() <= max_bytes {
        return text;
    }
    let mut end = max_bytes;
    while !text.is_char_boundary(end) {
        end -= 1;
    }
    &text[..end]
}

fn unix_millis() -> Result<u64> {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .context("system clock is before the Unix epoch")?
        .as_millis()
        .try_into()
        .context("Unix time exceeds u64")
}

#[cfg(test)]
mod tests;
