//! Typed CultCache records for Heimdall's answer collector, cut from `docs/answer-collector-consent.md`
//! in GameCult/Heimdall PR #6 (head d836f29): the notice acceptance record, the answer record, and
//! the terms version on the member. Field names follow the doc's tables. Where the doc is silent
//! the choice made here is listed in README.md as a question for Heimdall.

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

/// Separates key parts. Ids containing it are refused rather than escaped.
const SEP: char = '\u{1f}';

fn key(parts: &[&str]) -> Result<String> {
    if let Some(p) = parts.iter().find(|p| p.is_empty() || p.contains(SEP)) {
        bail!("key part {p:?} is empty or contains the key separator");
    }
    Ok(parts.join(&SEP.to_string()))
}

/// `<sample>@<version>`, version a positive integer. The version is part of the id, so it is part
/// of every answer key and no key can name a sample without one.
pub fn check_sample_id(sample_id: &str) -> Result<(&str, u64)> {
    let Some((sample, version)) = sample_id.rsplit_once('@') else {
        bail!("sample id {sample_id:?} has no @version");
    };
    match version.parse::<u64>() {
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
    /// `(experiment_id, member_id, notice_version)`.
    pub fn cache_key(experiment_id: &str, member_id: &str, notice_version: u32) -> Result<String> {
        key(&[experiment_id, member_id, &notice_version.to_string()])
    }
}

/// One answer. `notice_version` and `scope` are optional in the typed record only so that a stored
/// answer lacking them can be represented and excluded; the doc's table makes them required.
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
    /// `origin_server_ts` of the reply, unix milliseconds UTC.
    #[cultcache(key = 7)]
    pub received_at: i64,
    #[cultcache(key = 8)]
    pub event_id: String,
    /// The tombstone's salted hash of `text`, present only once exited. Not in the doc's table: see README.
    #[cultcache(key = 9, default)]
    pub text_hash: Option<Vec<u8>>,
}

impl Answer {
    /// `(experiment_id, sample_id, member_id)`; refuses a sample id without a positive version.
    pub fn cache_key(experiment_id: &str, sample_id: &str, member_id: &str) -> Result<String> {
        check_sample_id(sample_id)?;
        key(&[experiment_id, sample_id, member_id])
    }

    /// Redaction is exit: text erased, `exited` set, a hash-only tombstone kept. The hash is computed
    /// by the caller; this crate never sees it salted or unsalted.
    pub fn redacted(&self, text_hash: Vec<u8>) -> Answer {
        Answer { text: None, exited: true, text_hash: Some(text_hash), ..self.clone() }
    }

    /// Training rule: scope `train`, an acceptance for this notice version made no later than the
    /// answer, and not exited. No recorded scope, version or acceptance means excluded.
    pub fn is_training_data(&self, acceptance: Option<&NoticeAcceptance>) -> bool {
        let (Some(Scope::Train), Some(version), Some(acc)) = (self.scope, self.notice_version, acceptance) else {
            return false;
        };
        !self.exited
            && acc.notice_version == version
            && acc.experiment_id == self.experiment_id
            && acc.member_id == self.member_id
            && acc.scope == Scope::Train
            && acc.accepted_at <= self.received_at
    }
}

/// Newcomers only: the terms version recorded by the `m.login.terms` stage. Members who joined
/// before it, and Minds, have no record, and the collector never reads it or gates on it.
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
    /// `member_id`.
    pub fn cache_key(member_id: &str) -> Result<String> {
        key(&[member_id])
    }
}
