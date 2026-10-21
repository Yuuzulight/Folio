// These tests own STA message loops and cold-start real sandboxed processes.
// Run each lifecycle scenario alone, including on small CI runners.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
