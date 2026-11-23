// Fenstertests nacheinander: Mehrere STA-Threads mit eigenen Dispatchern stören sich sonst (Codex zu PR #55).
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
