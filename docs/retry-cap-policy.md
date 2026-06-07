# Worker retry-cap policy

Den orchestrator retry caps are **per role / per gate**, not total task attempts.

As of den-core #2078, the default `determine_orchestrator_next_action` cap is `max_attempts = 4`. Callers may still pass an explicit `max_attempts` override; for example, `max_attempts = 3` preserves the previous escalation boundary for a cautious or constrained workflow.

## What counts against the cap

Count structured worker attempts for the role/gate currently being evaluated:

- `coder`
- `reviewer`
- `validator`
- `drift_checker`
- `packet_auditor`

The cap is intended for normal implementation/review/validation loops where the same role can make bounded progress from Den packets and findings.

## What should not spend retry budget

Do not burn ordinary retry attempts on infrastructure or routing failures that a worker cannot fix by trying the task again:

- no worker claim / no-capacity events
- expired auth or missing credentials
- missing Den Channels membership or direct-agent route 404
- undeployed live-service state
- provider/model/config drift
- synthetic registration assignments superseded by concrete pool assignments

Those should block, split, or route to the owning service/operator with structured evidence rather than consuming the retry cap.

## Calibration guidance

Use retry-cap reports and packet history to tune policy:

- If a material share of successful tasks need the fourth attempt and human intervention is mostly rubber-stamping, keeping the default at 4 is justified.
- If fourth attempts usually fail, widen scope, or mask infrastructure problems, keep Planner escalation or lower the cap for that workflow via explicit override.
- Treat blocked deployment/auth/routing/membership categories separately from true retry pressure.
