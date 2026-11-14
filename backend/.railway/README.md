# Orcub Railway infrastructure

Run from `backend/`:

```sh
npm ci
railway config plan
railway config apply
railway up . --path-as-root --service api --environment production
```

The SDK is pinned in package-lock.json. The project already exists: `orcub`, production, with `api`, PostgreSQL 18, its persistent volume and the PITR backup bucket. The authoring file adopts these resources; it does not create a second database. Review every plan before applying, especially resource deletions or replacements. A fresh plan after the initial apply reported no changes.

Variables are deliberately unmanaged. ADMIN_TOKEN, DATABASE_URL and backup credentials remain in Railway; never import variables with `--include-variables` into checked-in files. The existing API database connection uses private networking. Keep PostgreSQL without a public TCP proxy.

API: Dockerfile builder, one replica in us-west2 beside PostgreSQL, always on, `/readyz` deploy healthcheck with 120-second timeout, 15-second overlap, 40-second termination drain, ON_FAILURE restart with 10 retries. Migrations run before HTTP starts. Upload from backend/ so Dockerfile is at the archive root.

Database recovery is managed through Railway's Postgres product commands, separately from the imported graph. PITR is enabled and the backup bucket wired; daily and weekly backup schedules are enabled. pgBackRest reported an OK repository with one backup. Do not remove the PITR bucket or persistent volume. Test restoration into a separate environment before relying on an RPO/RTO.

Custom domain `orcub.sathuashrith.com` still needs its Cloudflare DNS-only CNAME: `orcub` -> `m8g7crku.up.railway.app`. Railway then completes ownership validation and TLS. The Railway-provided API URL remains usable while DNS is pending.

Firebase Authentication, AdMob and Apple's platform services remain external. Current API storage needs only Postgres; no Redis, application object bucket or separate cron service is required. See ../README.md for retention, connection budgets and launch verification limits.

References: https://docs.railway.com/infrastructure-as-code and https://docs.railway.com/volumes/backups.
