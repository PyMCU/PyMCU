using NUnit.Framework;

// Run each test fixture in its own thread so independent firmware tests execute
// concurrently.  Tests within a fixture remain sequential (the shared SimSession
// instance is not thread-safe for parallel test methods) while fixtures are fully
// isolated from each other (each has its own ArduinoUnoSimulation and compiler
// cache entry).
[assembly: Parallelizable(ParallelScope.Fixtures)]
