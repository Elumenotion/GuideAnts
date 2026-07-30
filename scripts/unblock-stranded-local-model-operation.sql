-- Frees a router alias that is permanently blocked by a stranded lifecycle
-- operation: a row left on a non-terminal status that no reconciler owns, so
-- EnsureNoInFlightOperationAsync rejects every new attempt with
-- OPERATION_IN_FLIGHT and nothing can ever advance or fail the row.
--
-- Rows are marked failed rather than completed: the operation did not finish,
-- so provenance must not claim it did. Retry the operation after running this.

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @OperationId UNIQUEIDENTIFIER = '9B64CE9F-DB0B-4C6B-8709-25B6B3F2214E';

UPDATE LocalModelOperations
SET Status = 'failed',
    CurrentStep = 'failed',
    ErrorCode = 'INSTALL_STEP_FAILED',
    ErrorMessage = 'Operation was stranded on a non-terminal status that no reconciler owned.',
    Remediation = 'Retry the operation.',
    UpdatedUtc = SYSUTCDATETIME(),
    CompletedUtc = SYSUTCDATETIME()
WHERE OperationId = @OperationId
  AND Status NOT IN ('completed', 'failed');

SELECT OperationId, OperationKind, RouterModelId, Status, ErrorCode
FROM LocalModelOperations
WHERE RouterModelId = 'Qwen3.6-27B-MTP-GGUF';
