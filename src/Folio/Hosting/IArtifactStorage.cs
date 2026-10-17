namespace Folio;

using System.Collections.Generic;

/// <summary>Host-provided persistence for data owned by one Folio artifact.</summary>
public interface IArtifactStorage
{
    /// <summary>Reads a value previously written for this artifact, or null when it is absent.</summary>
    string? Read(string key);

    /// <summary>Writes or replaces a value for this artifact.</summary>
    void Write(string key, string value);

    /// <summary>Removes a value for this artifact.</summary>
    void Delete(string key);

    /// <summary>Lists keys beginning with a prefix.</summary>
    IReadOnlyList<string> Keys(string prefix);
}
