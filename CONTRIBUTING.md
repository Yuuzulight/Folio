# Contributing to Folio

## References

- Folio's design is its own. Code, comments, docs, commit messages and pull requests do not credit, link to, describe or say they were "inspired by" other rendering engines, browsers or similar projects.
- Specifications are the reference: link the spec section an algorithm implements.
- Data a specification requires is part of implementing it and is kept as the spec gives it, even when it names old products (for example the legacy DOCTYPE public identifiers that select quirks mode).
- Folio's own dependencies (SkiaSharp, HarfBuzzSharp, test libraries) may be named where they are used.

## Naming

- The public hosting API is in the `Folio` namespace (`Folio.Document`, `FolioOptions`, ...). The internal DOM document class is `Folio.Dom.DocumentNode`.
- Avoid a namespace and a type with the same name (`Folio.Painting` is the home of the planned `Paint` type, `Folio.Typography` sits beside the DOM's `Text`).

## Building and tests

See the Building section of the [README](README.md). Golden images change only through the approve command, and the new images are reviewed in the pull request.
