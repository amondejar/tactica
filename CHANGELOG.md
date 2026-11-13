# Changelog

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
