//! Fixed windows; expired keys are removed and a full table fails closed.
use std::{
    collections::HashMap,
    sync::Mutex,
    time::{Duration, Instant},
};
#[derive(Default)]
pub struct Limits(Mutex<Windows>);
#[derive(Default)]
struct Windows {
    keys: HashMap<String, (Instant, u32)>,
    sweep: Option<Instant>,
}
impl Limits {
    pub fn check(&self, key: String, cap: u32, seconds: u64) -> bool {
        let now = Instant::now();
        let mut windows = self.0.lock().unwrap();
        if windows.sweep.is_none_or(|t| t <= now) {
            windows.keys.retain(|_, (until, _)| *until > now);
            windows.sweep = Some(now + Duration::from_secs(60));
        }
        let keys = &mut windows.keys;
        if keys.get(&key).is_some_and(|(until, _)| *until <= now) {
            keys.remove(&key);
        }
        if !keys.contains_key(&key) && keys.len() >= 20000 {
            return false;
        }
        let (_, count) = keys
            .entry(key)
            .or_insert((now + Duration::from_secs(seconds), 0));
        if *count >= cap {
            return false;
        }
        *count += 1;
        true
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn isolation_expiry_and_bound() {
        let l = Limits::default();
        assert!(l.check("a".into(), 1, 60));
        assert!(!l.check("a".into(), 1, 60));
        assert!(l.check("b".into(), 1, 60));
        l.0.lock()
            .unwrap()
            .keys
            .insert("a".into(), (Instant::now(), 1));
        assert!(l.check("a".into(), 1, 60));
        for n in 0..20000 {
            l.check(format!("k{n}"), 1, 60);
        }
        assert_eq!(l.0.lock().unwrap().keys.len(), 20000);
        assert!(!l.check("overflow".into(), 1, 60));
    }
}
