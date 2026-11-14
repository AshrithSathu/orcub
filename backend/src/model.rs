use crate::{ApiError, Result};
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::collections::BTreeMap;
use uuid::Uuid;

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Screen {
    pub w: u32,
    pub h: u32,
    pub dpi: f64,
}
#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Install {
    pub install_id: Uuid,
    pub app_version: String,
    pub build: String,
    pub platform: String,
    pub os_version: String,
    pub device_model: String,
    pub device_ram_mb: u32,
    pub screen: Screen,
    pub locale: String,
    pub language: String,
    pub timezone: String,
    pub first_open_ts: DateTime<Utc>,
}
impl Install {
    pub fn validate(&mut self) -> Result<()> {
        for s in [
            &self.app_version,
            &self.build,
            &self.os_version,
            &self.device_model,
            &self.locale,
            &self.language,
            &self.timezone,
        ] {
            label(s, 128)?;
        }
        if !["ios", "android", "editor"].contains(&self.platform.as_str()) {
            return Err(ApiError::bad("invalid platform"));
        }
        self.device_ram_mb = self.device_ram_mb.min(131072);
        self.screen.w = self.screen.w.min(16384);
        self.screen.h = self.screen.h.min(16384);
        self.screen.dpi = self.screen.dpi.clamp(0., 2000.);
        timestamp(self.first_open_ts)
    }
}
pub fn label(s: &str, max: usize) -> Result<()> {
    if s.is_empty()
        || s.len() > max
        || !s
            .chars()
            .all(|c| c.is_alphanumeric() || "_.- /;=:+,".contains(c))
    {
        Err(ApiError::bad("invalid bounded label"))
    } else {
        Ok(())
    }
}
pub fn timestamp(t: DateTime<Utc>) -> Result<()> {
    if t > Utc::now() + chrono::Duration::minutes(5) || t < Utc::now() - chrono::Duration::days(400)
    {
        Err(ApiError::bad("timestamp outside ingestion window"))
    } else {
        Ok(())
    }
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Batch {
    pub player_id: Uuid,
    pub events: Vec<Event>,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Event {
    pub event_id: Uuid,
    pub kind: String,
    pub occurred_at: DateTime<Utc>,
    pub data: Value,
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "PascalCase")]
pub struct Attempt {
    pub version: u32,
    pub variant: u32,
    pub tuning_version: String,
    pub model_version: String,
    pub features: Features,
    pub model_prediction: f64,
    pub skill_estimate: f64,
    pub skill_confidence: f64,
    pub level_index: u32,
    pub started_utc: DateTime<Utc>,
    pub plan: Plan,
    pub level_rating: f64,
    pub rating_before: f64,
    pub rating_after: f64,
    pub result: AttemptResult,
}
#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "PascalCase")]
pub struct Features {
    pub moves: Vec<Move>,
    pub count: u32,
    pub useful: f64,
    pub think: f64,
    pub locked: f64,
    pub undo: f64,
}
#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "PascalCase")]
pub struct Move {
    pub useful: bool,
    pub undo: bool,
    pub think_seconds: f64,
    pub locked_touches: u32,
}
#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Plan {
    pub variant: u32,
    #[serde(rename = "extraMoves")]
    pub extra_moves: u32,
    #[serde(rename = "freeHintGlow")]
    pub free_hint_glow: bool,
    pub reason: String,
    #[serde(rename = "moveLimit")]
    pub move_limit: u32,
}
#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "PascalCase")]
pub struct AttemptResult {
    pub outcome: u8,
    pub moves_used: u32,
    pub seconds: f64,
    pub boosters_used: u32,
    pub continues: u32,
}
#[derive(Serialize, Deserialize)]
#[serde(
    tag = "kind",
    content = "data",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum KnownEvent {
    SessionStart {
        session_id: Uuid,
    },
    SessionEnd {
        session_id: Uuid,
        duration: f64,
    },
    LevelStart {
        #[serde(rename = "LevelIndex")]
        level: u32,
        #[serde(rename = "Variant")]
        variant: u32,
        #[serde(rename = "Plan")]
        plan: Plan,
    },
    LevelEnd(Box<Attempt>),
    BoosterUsed {
        level: u32,
        booster: String,
    },
    AdOffer(Ad),
    AdShown(Ad),
    AdReward(Ad),
    AdFailed {
        placement: String,
        network: String,
        code: String,
    },
    AdPaid {
        placement: String,
        network: String,
        value_micros: u64,
        currency: String,
        precision: String,
    },
    Iap {
        product_id: String,
        value_micros: u64,
        currency: String,
    },
    SettingsChanged {
        sound: bool,
        haptics: bool,
    },
    TutorialStep {
        step: u32,
        completed: bool,
    },
    Error {
        code: String,
        context: String,
    },
}
#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Ad {
    pub placement: String,
    pub network: String,
}
impl Event {
    pub fn validated(&self) -> Result<(Value, bool)> {
        if matches!(self.kind.as_str(), "ad_paid" | "iap")
            && self.occurred_at < Utc::now() - chrono::Duration::days(30)
        {
            return Err(ApiError::bad("revenue timestamp outside 30 days"));
        }
        timestamp(self.occurred_at)?;
        label(&self.kind, 64)?;
        let known = [
            "session_start",
            "session_end",
            "level_start",
            "level_end",
            "booster_used",
            "ad_offer",
            "ad_shown",
            "ad_reward",
            "ad_failed",
            "ad_paid",
            "iap",
            "settings_changed",
            "tutorial_step",
            "error",
        ]
        .contains(&self.kind.as_str());
        if !known {
            // Forward-compatible raw metrics only: no arbitrary strings or nested personal data.
            let map = self
                .data
                .as_object()
                .ok_or(ApiError::bad("raw data must be metric object"))?;
            if map.len() > 32 {
                return Err(ApiError::bad("too many raw metrics"));
            }
            for (k, v) in map {
                if !k.starts_with("metric_") {
                    return Err(ApiError::bad("unknown events allow metric_ keys only"));
                }
                label(k, 64)?;
                if !v.is_number() && !v.is_boolean() && !v.is_null() {
                    return Err(ApiError::bad("raw metrics must be numeric or boolean"));
                }
            }
            let mut data = self.data.clone();
            clamp(&mut data, "");
            return Ok((data, true));
        }
        let typed: KnownEvent = serde_json::from_value(json!({"kind":self.kind,"data":self.data}))
            .map_err(|_| ApiError::bad("event does not match typed schema"))?;
        if let KnownEvent::LevelEnd(a) = &typed {
            if a.version != 2 || a.result.outcome > 2 || a.features.moves.len() > 32 {
                return Err(ApiError::bad("invalid attempt schema"));
            }
            timestamp(a.started_utc)?;
        }
        if let KnownEvent::AdPaid {
            currency,
            precision,
            ..
        } = &typed
        {
            currency_code(currency)?;
            if !["unknown", "estimated", "publisher_provided", "precise"]
                .contains(&precision.as_str())
            {
                return Err(ApiError::bad("invalid precision"));
            }
        }
        if let KnownEvent::Iap { currency, .. } = &typed {
            currency_code(currency)?;
        }
        let mut data =
            serde_json::to_value(typed).map_err(|_| ApiError::bad("serialization"))?["data"].take();
        validate_labels(&data)?;
        clamp(&mut data, "");
        // Derive aggregate features from bounded per-move inputs; never trust client sums.
        if let Some(f) = data.get_mut("Features") {
            let moves = f["Moves"].as_array().expect("typed moves");
            let count = moves.len();
            let useful = moves.iter().filter(|m| m["Useful"] == true).count();
            let undo = moves.iter().filter(|m| m["Undo"] == true).count();
            let think: f64 = moves
                .iter()
                .map(|m| m["ThinkSeconds"].as_f64().unwrap_or(0.))
                .sum();
            let locked: f64 = moves
                .iter()
                .map(|m| m["LockedTouches"].as_f64().unwrap_or(0.))
                .sum();
            f["Count"] = json!(count);
            f["Useful"] = json!(useful);
            f["Undo"] = json!(undo);
            f["Think"] = json!(think);
            f["Locked"] = json!(locked);
        }
        Ok((data, false))
    }
}
fn currency_code(s: &str) -> Result<()> {
    if s.len() != 3 || !s.bytes().all(|b| b.is_ascii_uppercase()) {
        Err(ApiError::bad("currency must be ISO uppercase code"))
    } else {
        Ok(())
    }
}
fn validate_labels(v: &Value) -> Result<()> {
    match v {
        Value::String(s) => label(s, 256),
        Value::Object(m) => {
            for v in m.values() {
                validate_labels(v)?;
            }
            Ok(())
        }
        Value::Array(a) => {
            for v in a {
                validate_labels(v)?;
            }
            Ok(())
        }
        _ => Ok(()),
    }
}
fn clamp(v: &mut Value, key: &str) {
    match v {
        Value::Number(n) => {
            let max = match key {
                "ModelPrediction" | "SkillConfidence" => 1.,
                "RatingBefore" | "RatingAfter" | "LevelRating" | "SkillEstimate" => 2500.,
                "ThinkSeconds" => 30.,
                "LockedTouches" => 10.,
                "Seconds" | "duration" => 86400.,
                "value_micros" => 1e12,
                "LevelIndex" | "level" => 100000.,
                "Variant" | "variant" => 100.,
                "BoostersUsed" | "Continues" | "extraMoves" => 100.,
                _ => 1000000.,
            };
            let x = n.as_f64().unwrap_or(0.).clamp(0., max);
            if n.is_f64() {
                *v = json!(x)
            } else {
                *v = json!(x as u64)
            }
        }
        Value::Object(m) => {
            for (k, v) in m {
                clamp(v, k)
            }
        }
        Value::Array(a) => {
            for v in a {
                clamp(v, key)
            }
        }
        _ => {}
    }
}
#[derive(Clone, Serialize, Deserialize, Default)]
#[serde(deny_unknown_fields)]
pub struct SaveData {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub daily: Option<Value>,
    pub highest_level: u32,
    pub stars: BTreeMap<u32, u8>,
    #[serde(default, skip_serializing)]
    pub inventory: BTreeMap<String, u32>,
    pub workshop: BTreeMap<String, u32>,
    #[serde(default, skip_serializing)]
    pub lives: u32,
    pub settings: Settings,
    pub skill: Skill,
}
#[derive(Clone, Serialize, Deserialize, Default)]
#[serde(deny_unknown_fields)]
pub struct Settings {
    pub sound: bool,
    pub haptics: bool,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Skill {
    pub rating: f64,
    pub games: u32,
    pub recent: Vec<Sample>,
}
impl Default for Skill {
    fn default() -> Self {
        Self {
            rating: 1000.,
            games: 0,
            recent: Vec::new(),
        }
    }
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Sample {
    pub won: bool,
    pub efficiency: f64,
    pub seconds_per_move: f64,
    pub boosters: u32,
    pub continues: u32,
}
impl SaveData {
    pub fn validate(&mut self) -> Result<()> {
        if let Some(d) = &self.daily {
            validate_daily(d)?;
        }
        if self.stars.len() > 100000
            || self.inventory.len() > 64
            || self.workshop.len() > 64
            || self.skill.recent.len() > 8
        {
            return Err(ApiError::bad("save collection too large"));
        }
        self.highest_level = self.highest_level.min(100000);
        self.lives = self.lives.min(100);
        for (level, s) in &mut self.stars {
            if *level > 100000 {
                return Err(ApiError::bad("invalid level"));
            }
            *s = (*s).min(3);
        }
        for (k, v) in self.inventory.iter_mut().chain(self.workshop.iter_mut()) {
            label(k, 64)?;
            *v = (*v).min(1000000);
        }
        self.skill.rating = self.skill.rating.clamp(100., 2500.);
        self.skill.games = self.skill.games.min(1_000_000);
        for s in &mut self.skill.recent {
            s.efficiency = s.efficiency.clamp(0., 1.);
            s.seconds_per_move = s.seconds_per_move.clamp(0., 86400.);
            s.boosters = s.boosters.min(100);
            s.continues = s.continues.min(100);
        }
        Ok(())
    }
    pub fn merge_progress(&mut self, old: &Self) {
        if self.daily.is_none() {
            self.daily = old.daily.clone();
        } else if let (Some(current), Some(previous)) = (&mut self.daily, &old.daily) {
            for key in ["Completed", "PendingPuzzleGrants"] {
                let mut days = std::collections::BTreeSet::new();
                for section in [&*current, previous] {
                    if let Some(array) = section[key].as_array() {
                        days.extend(array.iter().filter_map(Value::as_str).map(str::to_owned));
                    }
                }
                current[key] = json!(
                    days.into_iter()
                        .rev()
                        .take(3660)
                        .collect::<std::collections::BTreeSet<_>>()
                );
            }
        }
        self.highest_level = self.highest_level.max(old.highest_level);
        for (k, v) in &old.stars {
            let s = self.stars.entry(*k).or_default();
            *s = (*s).max(*v);
        }
    }
}
fn validate_daily(v: &Value) -> Result<()> {
    let obj = v.as_object().ok_or(ApiError::bad("daily object"))?;
    if v["Version"] != 1 {
        return Err(ApiError::bad("daily version"));
    }
    for (k, v) in obj {
        let valid = match k.as_str() {
            "Version" => v == 1,
            "HighestUtcDay"
            | "LastPuzzleDay"
            | "LastGiftDay"
            | "LastNotificationDay"
            | "ScheduledDay" => v
                .as_str()
                .is_some_and(|s| s.is_empty() || crate::economy::daily_date(s)),
            "Completed" | "PendingPuzzleGrants" => v.as_array().is_some_and(|a| {
                let mut unique = std::collections::HashSet::new();
                a.len() <= 3660
                    && a.iter().all(|v| {
                        v.as_str()
                            .is_some_and(|s| crate::economy::daily_date(s) && unique.insert(s))
                    })
            }),
            "PuzzleStreak" | "Sessions" => v
                .as_i64()
                .is_some_and(|n| (0..=i32::MAX as i64).contains(&n)),
            "WinStreak" => v.as_i64().is_some_and(|n| (0..=3).contains(&n)),
            "BonusRotation" => v.as_i64().is_some_and(|n| (0..=2).contains(&n)),
            "LastOptInOffer" | "ScheduledAt" => v.as_i64().is_some_and(|n| n >= 0),
            "Freeze"
            | "FreezeEarned"
            | "BonusPending"
            | "FirstDailyCompletion"
            | "ActiveContinued"
            | "NotificationsEnabled"
            | "PermissionRequested" => v.is_boolean(),
            "ActiveAttempt" | "LastEndedAttempt" | "ActiveBonus" | "ScheduledId"
            | "LastOpenedId" => v.as_str().is_some_and(|s| s.len() <= 128),
            _ => false,
        };
        if !valid {
            return Err(ApiError::bad("daily field"));
        }
    }
    if let Some(pending) = v["PendingPuzzleGrants"].as_array()
        && pending
            .iter()
            .any(|p| !v["Completed"].as_array().is_some_and(|a| a.contains(p)))
    {
        return Err(ApiError::bad("daily pending not completed"));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn progress_never_regresses() {
        let mut old = SaveData {
            highest_level: 8,
            ..Default::default()
        };
        old.stars.insert(2, 3);
        let mut next = SaveData::default();
        next.merge_progress(&old);
        assert_eq!(next.highest_level, 8);
        assert_eq!(next.stars[&2], 3);
    }
    #[test]
    fn revenue_backdating_is_bounded() {
        let e = Event {
            event_id: Uuid::new_v4(),
            kind: "ad_paid".into(),
            occurred_at: Utc::now() - chrono::Duration::days(31),
            data: json!({}),
        };
        assert!(e.validated().is_err());
    }
    #[test]
    fn raw_pii_rejected() {
        let e = Event {
            event_id: Uuid::new_v4(),
            kind: "future".into(),
            occurred_at: Utc::now(),
            data: json!({"email":"hello@example.com"}),
        };
        assert!(e.validated().is_err());
    }
    #[test]
    fn unknown_metrics_bounded() {
        let e = Event {
            event_id: Uuid::new_v4(),
            kind: "future".into(),
            occurred_at: Utc::now(),
            data: json!({"metric_count":-50}),
        };
        assert_eq!(e.validated().unwrap().0["metric_count"], 0);
    }
}

#[cfg(test)]
mod daily_merge_tests {
    use super::*;
    #[test]
    fn older_saves_preserve_daily_and_link_unions_pending() {
        let guest = SaveData {
            daily: Some(
                json!({"Version":1,"Completed":["2026-11-14"],"PendingPuzzleGrants":["2026-11-14"]}),
            ),
            ..Default::default()
        };
        let mut old_build = SaveData::default();
        old_build.merge_progress(&guest);
        assert_eq!(old_build.daily, guest.daily);
        let mut account = SaveData {
            daily: Some(
                json!({"Version":1,"Completed":["2026-11-15"],"PendingPuzzleGrants":["2026-11-15"]}),
            ),
            ..Default::default()
        };
        account.merge_progress(&guest);
        assert_eq!(
            account.daily.as_ref().unwrap()["PendingPuzzleGrants"],
            json!(["2026-11-14", "2026-11-15"])
        );
    }
}
