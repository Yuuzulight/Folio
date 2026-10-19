namespace Folio;

using System.Collections.Generic;

/// <summary>Host-provided persistence for data owned by one Folio artifact.</summary>
public interface IArtifactStorage
{
    /// <summary>Reads a value previously written for this artifact, or null when it is absent.</summary>
    string? Read(string key);

    /// <summary>
    /// Atomically writes or replaces one value. If this throws, the previous value must remain intact.
    /// Folio commits a Cache API batch with one replacement before publishing its in-memory state.
    /// </summary>
    void Write(string key, string value);

    /// <summary>Removes a value for this artifact.</summary>
    void Delete(string key);

    /// <summary>Lists keys beginning with a prefix.</summary>
    IReadOnlyList<string> Keys(string prefix);
}
