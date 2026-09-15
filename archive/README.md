# Source archive

`v1/source.zip` contains the original source and README at commit
`cb6be55ecf22884e133e9cc9c3dcdd49d252b967`. It is excluded from compilation.
The archive project preserves the original allocating stream-copy routine for
BenchmarkDotNet comparisons. GitHub Releases retain versioned binary packages.

`benchmarks/2026-09-11/` contains exploratory ShortRun measurements on the machine named
in each report. These are local observations, not production sizing guarantees. The pooled
copy's measured hot loop reached zero managed allocation; native allocations are not counted.
Network round-trip allocations include the client/echo harness and task coordination.
The network run overlapped local packaging and should be repeated on an idle machine before
drawing performance comparisons. No dedicated-thread speed advantage was established.
