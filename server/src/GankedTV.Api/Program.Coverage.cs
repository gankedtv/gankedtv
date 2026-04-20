using System.Diagnostics.CodeAnalysis;

// Program.cs is DI wiring + environment-driven config fallbacks. Its runtime behavior is
// covered end-to-end by the WebApplicationFactory integration tests (every endpoint under
// Integration/Endpoints/ boots the full container through this file). Branch-level coverage
// here measures exercised `?? env ?? cfg ?? default` chains, not behavior — and hitting
// every combination would require a zoo of single-purpose boot factories with partial env.
// Exclude it and keep trust in the downstream tests instead.
//
// ASP0027 would normally warn on `public partial class Program` because .NET 6+ auto-exposes
// the generated Program to test projects. We keep the declaration here specifically so we can
// attach [ExcludeFromCodeCoverage]; `public` is retained because removing it flips Program to
// internal and breaks WebApplicationFactory<Program> in the test project.
#pragma warning disable ASP0027
[ExcludeFromCodeCoverage(Justification = "Bootstrap/DI wiring exercised via WebApplicationFactory integration tests")]
public partial class Program;
#pragma warning restore ASP0027
