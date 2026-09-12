// Tests run in parallel per class. Ambient static settings stay isolated
// through flow-scoped overrides (see FlowLocal<T> and GlobalStateGuard).
// The only exception is the "Serial" collection below, reserved for tests
// that mutate process-global resources AsyncLocal cannot isolate.
[assembly: Xunit.CollectionBehavior(Xunit.CollectionBehavior.CollectionPerClass)]
