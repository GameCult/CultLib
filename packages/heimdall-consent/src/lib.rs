//! Typed CultCache records for Heimdall's answer collector, cut from `docs/answer-collector-consent.md`
//! in GameCult/Heimdall PR #6 (doc commit 4e99bfa): the notice acceptance record, the answer record,
//! and the typed shape of the terms pair on the member record. Field names and types follow the
//! doc's tables and its Representation section; README.md cites the doc lines.

use anyhow::{Result, bail};
use cultcache_rs::DatabaseEntry;
use serde::{Deserialize, Serialize};

/// What a notice allows, each level including the ones below it. `Train` is declared in terms by the
/// room's notice; nothing here widens a scope.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Scope {
    Show,
    Study,
    Train,
}

/// Separates key parts (Representation: parts joined with U+001F; an empty part or one containing it
/// is refused, not escaped).
const SEP: char = '\u{1f}';

fn key(parts: &[&str]) -> Result<String> {
    if let Some(p) = parts.iter().find(|p| p.is_empty() || p.contains(SEP)) {
        bail!("key part {p:?} is empty or contains the key separator");
    }
    Ok(parts.join(&SEP.to_string()))
}

fn nonempty(name: &str, value: &str) -> Result<()> {
    if value.is_empty() {
        bail!("{name} must be a non-empty string");
    }
    Ok(())
}

/// `<sample>@<version>`, version a positive unsigned 32-bit integer. The version is part of the id,
/// so it is part of every answer key and no key can name a sample without one.
pub fn check_sample_id(sample_id: &str) -> Result<(&str, u32)> {
    let Some((sample, version)) = sample_id.rsplit_once('@') else {
        bail!("sample id {sample_id:?} has no @version");
    };
    match version.parse::<u32>() {
        Ok(v) if v > 0 && !sample.is_empty() && !version.starts_with('+') => Ok((sample, v)),
        _ => bail!("sample id {sample_id:?} needs a non-empty sample and a positive integer version"),
    }
}

/// The first reply a member posts under a notice version. Written once, never updated.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "heimdall.notice-acceptance", schema = "HeimdallNoticeAcceptance")]
pub struct NoticeAcceptance {
    #[cultcache(key = 0)]
    pub member_id: String,
    #[cultcache(key = 1)]
    pub experiment_id: String,
    #[cultcache(key = 2)]
    pub notice_version: u32,
    #[cultcache(key = 3)]
    pub scope: Scope,
    /// `origin_server_ts` of the first reply, unix milliseconds UTC.
    #[cultcache(key = 4)]
    pub accepted_at: i64,
    #[cultcache(key = 5)]
    pub event_id: String,
}

impl NoticeAcceptance {
    /// Representation: ids are non-empty strings, versions positive.
    pub fn validate(&self) -> Result<()> {
        nonempty("member_id", &self.member_id)?;
        nonempty("experiment_id", &self.experiment_id)?;
        nonempty("event_id", &self.event_id)?;
        if self.notice_version == 0 {
            bail!("notice_version must be positive");
        }
        Ok(())
    }

    /// `(experiment_id, member_id, notice_version)`.
    pub fn cache_key(experiment_id: &str, member_id: &str, notice_version: u32) -> Result<String> {
        key(&[experiment_id, member_id, &notice_version.to_string()])
    }
}

/// One answer. `notice_version` and `scope` are absent together on a reply with no established
/// notice version or no matching acceptance: such a reply is stored, excluded from every use, and
/// writes no acceptance (Answer record, "A reply whose `notice_version` or `scope` is missing").
/// Once exited the record is the tombstone: `text` absent, `text_hash` set.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "heimdall.answer", schema = "HeimdallAnswer")]
pub struct Answer {
    #[cultcache(key = 0)]
    pub sample_id: String,
    #[cultcache(key = 1)]
    pub experiment_id: String,
    #[cultcache(key = 2)]
    pub member_id: String,
    #[cultcache(key = 3, default)]
    pub notice_version: Option<u32>,
    #[cultcache(key = 4, default)]
    pub scope: Option<Scope>,
    /// Absent once exited.
    #[cultcache(key = 5, default)]
    pub text: Option<String>,
    /// One-way: set by redaction, never cleared.
    #[cultcache(key = 6, default)]
    pub exited: bool,
    /// Assigned by the collector on receipt, unix milliseconds UTC, not copied from the reply (doc L75);
    /// ties are broken by `supersedes`.
    #[cultcache(key = 7)]
    pub received_at: i64,
    #[cultcache(key = 8)]
    pub event_id: String,
    /// The tombstone hash of `text`: HMAC-SHA-256, 32 raw bytes, computed by the member-side service
    /// (it holds the salt). Present only once exited, and only if the collector held `text`.
    #[cultcache(key = 9, default)]
    pub text_hash: Option<Vec<u8>>,
}

impl Answer {
    /// `(experiment_id, sample_id, member_id)`; refuses a sample id without a positive version.
    pub fn cache_key(experiment_id: &str, sample_id: &str, member_id: &str) -> Result<String> {
        check_sample_id(sample_id)?;
        key(&[experiment_id, sample_id, member_id])
    }

    /// Representation and the invariants the table implies.
    pub fn validate(&self) -> Result<()> {
        nonempty("member_id", &self.member_id)?;
        nonempty("experiment_id", &self.experiment_id)?;
        nonempty("event_id", &self.event_id)?;
        check_sample_id(&self.sample_id)?;
        if self.notice_version == Some(0) {
            bail!("notice_version must be positive");
        }
        if self.notice_version.is_some() != self.scope.is_some() {
            bail!("notice_version and scope are present together or absent together");
        }
        if self.exited && self.text.is_some() {
            bail!("an exited answer holds no text");
        }
        if !self.exited && self.text.as_deref().is_none_or(str::is_empty) {
            bail!("a live answer needs non-empty text; empty text is valid only on a revoked answer");
        }
        if let Some(hash) = &self.text_hash {
            if !self.exited || hash.len() != 32 {
                bail!("text_hash is 32 bytes and only on an exited answer");
            }
        }
        Ok(())
    }

    /// Replacement rule for two answers at one key: the later reply is the greater `(received_at,
    /// event_id)`. `received_at` is assigned by the collector; a tie breaks by `event_id` compared
    /// bytewise, the greater winning (Heimdall PR #6 comment 759). Insertion order never decides.
    pub fn supersedes(&self, earlier: &Answer) -> bool {
        (self.received_at, self.event_id.as_bytes()) > (earlier.received_at, earlier.event_id.as_bytes())
    }

    /// Redaction is exit: text erased, `exited` set, the tombstone hash kept. The hash comes from the
    /// member-side service, which holds the salt; this crate never computes it. A reply the collector
    /// never collected has no text and gets no tombstone, so an answer without `text` is refused.
    pub fn redacted(&self, text_hash: [u8; 32]) -> Result<Answer> {
        if self.text.is_none() {
            bail!("no text held for event {:?}: nothing to tombstone", self.event_id);
        }
        Ok(Answer { text: None, exited: true, text_hash: Some(text_hash.to_vec()), ..self.clone() })
    }

    /// Training rule (doc "Training rule"): scope `train`; an acceptance for this answer's
    /// `(experiment_id, member_id, notice_version)` whose own scope is `train` and equals the answer's,
    /// with `accepted_at <= received_at`; not exited. A changed notice version needs its own acceptance,
    /// so an acceptance for another version never covers the answer. No recorded scope, version or
    /// acceptance means excluded.
    pub fn is_training_data(&self, acceptance: Option<&NoticeAcceptance>) -> bool {
        let (Some(Scope::Train), Some(version), Some(acc)) = (self.scope, self.notice_version, acceptance) else {
            return false;
        };
        !self.exited
            && acc.notice_version == version
            && acc.experiment_id == self.experiment_id
            && acc.member_id == self.member_id
            && acc.scope == Scope::Train
            && Some(acc.scope) == self.scope
            && acc.accepted_at <= self.received_at
    }
}

/// The typed shape of the `terms_version` / `terms_accepted_at` pair on the Heimdall member record
/// (newcomers only, recorded by the `m.login.terms` stage). It is not a second store and the
/// collector does not hold it. Members who joined before the stage, and Minds, have no such pair.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "heimdall.member-terms", schema = "HeimdallMemberTerms")]
pub struct MemberTerms {
    #[cultcache(key = 0)]
    pub member_id: String,
    #[cultcache(key = 1)]
    pub terms_version: String,
    /// Unix milliseconds UTC.
    #[cultcache(key = 2)]
    pub terms_accepted_at: i64,
}

impl MemberTerms {
    pub fn validate(&self) -> Result<()> {
        nonempty("member_id", &self.member_id)?;
        nonempty("terms_version", &self.terms_version)
    }

    /// `member_id`.
    pub fn cache_key(member_id: &str) -> Result<String> {
        key(&[member_id])
    }
}
