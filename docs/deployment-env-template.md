# Den Core deployment / env template

## Production env on den-srv

Location: `/data/services/den-core/env/server.env`

### Current state (as of #2560 fix)

The production env already has both `DenCore__*` and `DenMcp__*` (legacy) keys.
With the `ConfigMerger` fix, `DenMcp__*` env vars are no longer required — they
are merged as a compatibility fallback when no `DenCore__*` env override exists.

### Required keys

| Key | Value | Purpose |
|-----|-------|---------|
| `DenCore__ListenUrl` | `http://127.0.0.1:5299` | Internal listen port. Den Core MUST NOT listen on 5199 (owned by den-mcp facade). |
| `DenCore__DatabasePath` | `/data/services/den-core/data/den.db` | Production DB path. Not under `~/.den-core/`. |

### Optional / nested keys

| Key | Value | Purpose |
|-----|-------|---------|
| `DenCore__Llm__Endpoint` | `https://api.deepseek.com` | Librarian LLM endpoint |
| `DenCore__Llm__ApiKey` | `sk-...` | LLM API secret |
| `DenCore__Llm__Model` | `deepseek-v4-flash` | LLM model name |
| `DenCore__TrustedPublisher__AllowedOrchestrators__0` | `den-mcp-runner` | Allowed orchestrators |
| `DenCore__DenPublishFacade__Endpoint` | `http://127.0.0.1:5090` | Publish facade endpoint |
| `DenCore__GatewayContract__ServiceToken` | (optional) | Gateway token |

### Legacy keys (no longer required, kept for compatibility)

`DenMcp__*` keys still work via `ConfigMerger` fallback. Remove them as part
of routine cleanup after verifying `DenCore__*` equivalents are set.

### Migration from DenMcp__* to DenCore__*

```
# Old (still supported):
DenMcp__ListenUrl=http://127.0.0.1:5299
DenMcp__DatabasePath=/data/services/den-core/data/den.db

# New (preferred):
DenCore__ListenUrl=http://127.0.0.1:5299
DenCore__DatabasePath=/data/services/den-core/data/den.db
```

Simply add the `DenCore__*` keys to `server.env` and keep the legacy `DenMcp__*`
keys until the next deploy cycle. `ConfigMerger` handles both.

## Production validation

Den Core supports `--validate-prod` CLI flag for startup validation:

```bash
dotnet DenCore.Service.dll --validate-prod
```

This checks:
- ListenUrl is NOT on port 5199 (facade-owned)
- DatabasePath resolves under `/data/services/den-core/data/`

If validation fails, the process exits with code 1.

## Deploy smoke check

After deploy, run:

```bash
bash scripts/den-core-deploy-smoke.sh
```

This verifies:
- ✅ Core private health at `127.0.0.1:5299`
- ✅ Facade health at `192.168.1.10:5199`
- ✅ Projects endpoint returns real data (not empty DB)
- ✅ Knowledge routes accessible (where deployed)
- ✅ Static UI serves at root
