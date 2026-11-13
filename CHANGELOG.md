# Changelog

## [0.2.0-preview.1] - 2026-11-13

### Added

- Automatic fixes, in the IDE light bulb (one finding, or all in a document, project or solution) and in
  `dotnet format analyzers --diagnostics <ids>`:
  - TAC001: make the setter or field private.
  - TAC002: move the collection to a private field and expose a read-only view; uses inside the type move to the field.
  - TAC005: make the parameterless constructor private, or add a private one.
  - TAC010: remove the value object's setter, or make the field readonly.
  - TAC012: add the missing member to `GetEqualityComponents()`.
  - TAC013: generate `Equals` and `GetHashCode`.
  - TAC030: turn the event's `set` into `init`, or make the field readonly.
- The fixes ship in `Tactica.CodeFixes.dll`, next to the analyzers in the same package.

## [0.1.0-preview.1] - 2026-11-13

First preview.

- Analyzers for aggregates and entities (TAC001 setters, TAC002 mutable collections, TAC003 aggregates held by
  object, TAC005 public parameterless constructors, TAC006 anemic aggregates), value objects (TAC010 setters,
  TAC011 changes after creation, TAC012 members missing from equality, TAC013 reference equality), repositories
  (TAC020 inner entities, TAC021 `IQueryable`) and domain events (TAC030 setters, TAC031 raised from outside).
- Recognizes the usual base types and attributes by name; your own through `TacticaAggregateRoots` and the other
  settings.
- Messages say what to write instead, and can point to an example file of the repository.
- `tactica.baseline` and `dotnet tactica baseline` (Tactica.Tool) for existing code.
