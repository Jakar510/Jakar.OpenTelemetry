using Jakar.Json;

// Wire format of the HTTP snapshot and the [JsonModel] DTOs: camelCase (as with the Newtonsoft settings this replaces; nulls are written, since the records' constructor parameters are required),
// names matched case-insensitively when reading, NaN/Infinity written as strings instead of failing the response, and the records keep their own ToString.
[assembly: JsonDefaults( Naming = JsonNaming.CamelCase, NameMatching = JsonNameMatching.OrdinalIgnoreCase, NonFiniteFloats = JsonNonFiniteFloats.AsString, GenerateToString = JsonToggle.Off )]
