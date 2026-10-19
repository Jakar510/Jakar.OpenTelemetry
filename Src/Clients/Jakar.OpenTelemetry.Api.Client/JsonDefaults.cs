using Jakar.Json;

// Durable state files (pending images, crash reports, the session marker): camelCase as System.Text.Json wrote them before, so files queued by an older version still read.
[assembly: JsonDefaults( Naming = JsonNaming.CamelCase, NameMatching = JsonNameMatching.OrdinalIgnoreCase, GenerateToString = JsonToggle.Off )]
