# Database changelist migrations

Database schema changes live in `documents/database/changelist/`. Name each SQL
file with its date and a short description. Deployments enumerate these files in
sorted repository-path order.

The deploy runner is `deploy/apply-migrations.sh`. It uses the target host's
`mysql` client and its `~/.my.cnf` credentials. The runner creates
`HappyNotes.SchemaChangelog`, applies only paths that are not recorded there, and
records each successful script with its application timestamp.

`deploy/migrations-baseline.txt` is an immutable snapshot of the changelists
already applied when tracking was introduced. The first run records every
baseline path without executing its SQL. This protects historical scripts that
drop tables or perform data backfills. Do not add new files to the baseline;
new changelists must be recorded by the runner instead.

Production runs the runner from the checked-out repository. Staging transfers
the runner, baseline, and changelist directory to a temporary host directory
and runs the same script there. Both workflows complete migration before
restarting the new API process. A SQL or changelog error exits the workflow and
there is no service restart for that deployment.

To validate the orchestration locally without a database:

```bash
bash deploy/tests/apply-migrations.test.sh
```
