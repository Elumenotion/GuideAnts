# Lifecycle authority tests

The contract is protected at three layers:

- `test_warmup_plan.py` rejects incomplete or implicit plans and proves that
  disabled inventory references do not request a load.
- `test_warmup_orchestrator.py` proves container startup produces empty/idle
  status without engine calls and verifies ordered execution only after a plan
  is submitted.
- `LocalAiStartupWarmupServiceTests` proves cloud routing disables local image
  generation and skips bundle projection.
- `LocalAiDesiredStateBuilderTests` proves ServiceModes produce explicit JSON
  lifecycle state and missing local selections fail.
- `LocalServiceModeSelectionReaderTests` proves selection reads do not mutate
  ServiceModes.
- `LocalAiLifecycleAuthorityContractTests` scans runtime/configuration sources
  for removed INI/autoload/backfill mechanisms.

Any change that restores container autoload, a persisted desired plan, or
engine-to-ServiceModes synchronization must fail tests.
