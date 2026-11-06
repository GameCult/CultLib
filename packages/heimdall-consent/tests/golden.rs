use cultcache_rs::{CultCache, SingleFileMessagePackBackingStore};
use heimdall_consent::*;

const VECTORS: &str = include_str!("vectors/consent_golden.csv");

fn new_cache(dir: &tempfile::TempDir) -> CultCache {
    let mut cache = CultCache::new();
    cache.register_entry_type::<NoticeAcceptance>().unwrap();
    cache.register_entry_type::<Answer>().unwrap();
    cache.register_entry_type::<MemberTerms>().unwrap();
    let _ = cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(dir.path().join("consent.cc")));
    cache.pull_all_backing_stores().unwrap();
    cache
}

fn scope(s: &str) -> Option<Scope> {
    match s {
        "" => None,
        "show" => Some(Scope::Show),
        "study" => Some(Scope::Study),
        "train" => Some(Scope::Train),
        other => panic!("unknown scope {other}"),
    }
}

fn hex(s: &str) -> Vec<u8> {
    (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
}

fn put_answer(cache: &mut CultCache, a: &Answer) {
    cache.put(Answer::cache_key(&a.experiment_id, &a.sample_id, &a.member_id).unwrap(), a).unwrap();
}

fn training_events(cache: &CultCache) -> Vec<String> {
    let mut ids: Vec<String> = cache
        .get_all::<Answer>()
        .unwrap()
        .iter()
        .filter(|a| {
            let acc = a.notice_version.and_then(|v| {
                cache.get::<NoticeAcceptance>(&NoticeAcceptance::cache_key(&a.experiment_id, &a.member_id, v).unwrap()).unwrap()
            });
            a.is_training_data(acc.as_ref())
        })
        .map(|a| a.event_id.clone())
        .collect();
    ids.sort();
    ids
}

#[test]
fn golden_vectors() {
    let mut cases: Vec<(&str, Vec<Vec<&str>>)> = Vec::new();
    for line in VECTORS.lines().filter(|l| !l.starts_with('#') && !l.trim().is_empty()) {
        let f: Vec<&str> = line.split(',').collect();
        assert_eq!(f.len(), 10, "row `{line}`");
        match cases.last_mut() {
            Some((name, rows)) if *name == f[0] => rows.push(f),
            _ => cases.push((f[0], vec![f])),
        }
    }
    assert_eq!(cases.len(), 4, "fixture shrank");
    for (name, rows) in cases {
        let dir = tempfile::tempdir().unwrap();
        let mut cache = new_cache(&dir);
        for f in rows {
            let (op, member, room, nv, sc, sample, payload, ts, event) = (f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9]);
            let ctx = format!("case {name} op {op} event {event}");
            let ts: i64 = ts.parse().unwrap_or(0);
            let nv: Option<u32> = nv.parse().ok();
            match op {
                "reply" | "answer_raw" => {
                    if op == "reply" {
                        if let (Some(v), Some(s)) = (nv, scope(sc)) {
                            let k = NoticeAcceptance::cache_key(room, member, v).unwrap();
                            if cache.get::<NoticeAcceptance>(&k).unwrap().is_none() {
                                cache
                                    .put(k, &NoticeAcceptance {
                                        member_id: member.into(),
                                        experiment_id: room.into(),
                                        notice_version: v,
                                        scope: s,
                                        accepted_at: ts,
                                        event_id: event.into(),
                                    })
                                    .unwrap();
                            }
                        }
                    }
                    put_answer(&mut cache, &Answer {
                        sample_id: sample.into(),
                        experiment_id: room.into(),
                        member_id: member.into(),
                        notice_version: nv,
                        scope: scope(sc),
                        text: Some(payload.into()),
                        exited: false,
                        received_at: ts,
                        event_id: event.into(),
                        text_hash: None,
                    });
                }
                "redact" => {
                    let k = Answer::cache_key(room, sample, member).unwrap();
                    let a = cache.get_required::<Answer>(&k).unwrap();
                    assert_eq!(a.event_id, event, "{ctx}");
                    put_answer(&mut cache, &a.redacted(hex(payload)));
                }
                "terms" => {
                    cache
                        .put(MemberTerms::cache_key(member).unwrap(), &MemberTerms {
                            member_id: member.into(),
                            terms_version: payload.into(),
                            terms_accepted_at: ts,
                        })
                        .unwrap();
                }
                "expect_counts" => {
                    let want = format!(
                        "answers={};acceptances={}",
                        cache.get_all::<Answer>().unwrap().len(),
                        cache.get_all::<NoticeAcceptance>().unwrap().len()
                    );
                    assert_eq!(want, payload, "{ctx}");
                }
                "expect_train" => assert_eq!(training_events(&cache).join(";"), payload, "{ctx}"),
                "expect_exited" => {
                    let a = cache.get_required::<Answer>(&Answer::cache_key(room, sample, member).unwrap()).unwrap();
                    assert!(a.exited && a.text.is_none(), "{ctx}");
                    assert_eq!(a.text_hash, Some(hex(payload)), "{ctx}");
                    assert_eq!((a.event_id.as_str(), a.received_at, a.sample_id.as_str()), (event, ts, sample), "{ctx}");
                }
                "expect_acceptance" => {
                    let a = cache
                        .get_required::<NoticeAcceptance>(&NoticeAcceptance::cache_key(room, member, nv.unwrap()).unwrap())
                        .unwrap();
                    assert_eq!((a.notice_version, Some(a.scope), a.accepted_at), (nv.unwrap(), scope(sc), ts), "{ctx}");
                    assert_eq!(a.event_id, payload, "{ctx}");
                }
                other => panic!("unknown op {other}"),
            }
        }
    }
}

#[test]
fn records_survive_a_reopen() {
    let dir = tempfile::tempdir().unwrap();
    let mut cache = new_cache(&dir);
    let a = Answer {
        sample_id: "s@1".into(),
        experiment_id: "r".into(),
        member_id: "m".into(),
        notice_version: Some(2),
        scope: Some(Scope::Train),
        text: None,
        exited: true,
        received_at: 5,
        event_id: "e".into(),
        text_hash: Some(vec![1, 2]),
    };
    put_answer(&mut cache, &a);
    let reopened = new_cache(&dir);
    assert_eq!(reopened.get_required::<Answer>(&Answer::cache_key("r", "s@1", "m").unwrap()).unwrap(), a);
}

#[test]
fn key_refuses_unversioned_sample_ids() {
    for bad in ["s", "s@", "s@0", "s@x", "@1", "s@-1", "s@+1"] {
        assert!(Answer::cache_key("r", bad, "m").is_err(), "{bad}");
    }
    assert!(Answer::cache_key("r", "s@1", "m").unwrap() != Answer::cache_key("r", "s@2", "m").unwrap());
}

#[test]
fn acceptance_after_the_answer_does_not_cover_it() {
    let a = Answer {
        sample_id: "s@1".into(),
        experiment_id: "r".into(),
        member_id: "m".into(),
        notice_version: Some(1),
        scope: Some(Scope::Train),
        text: Some("t".into()),
        exited: false,
        received_at: 100,
        event_id: "e".into(),
        text_hash: None,
    };
    let acc = |at| NoticeAcceptance {
        member_id: "m".into(),
        experiment_id: "r".into(),
        notice_version: 1,
        scope: Scope::Train,
        accepted_at: at,
        event_id: "e".into(),
    };
    assert!(a.is_training_data(Some(&acc(100))));
    assert!(!a.is_training_data(Some(&acc(101))));
    assert!(!a.is_training_data(None));
}
