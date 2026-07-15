using Xunit;

// GitHub's two-core Windows runners showed starvation and transient file-lock
// failures when more than 800 process, timer, database, and UI tests ran across
// classes in parallel. Product-level concurrency remains covered inside the
// individual tests; serializing test classes makes release validation repeatable.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
