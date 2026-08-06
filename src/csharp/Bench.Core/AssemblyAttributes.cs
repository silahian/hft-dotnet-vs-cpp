// Suppresses the JIT's zero-initialization of locals. Free when every local is
// definitely assigned before use, which is the case in both workloads.
[module: System.Runtime.CompilerServices.SkipLocalsInit]
