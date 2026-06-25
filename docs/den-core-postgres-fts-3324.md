# Postgres full-text search notes for task 3324

Task #3324 replaced SQLite FTS5 document and knowledge searches with Postgres
full-text search for the live provider. SQLite FTS5 behavior remains only for
legacy test fixtures and rollback archaeology.

Postgres uses expression GIN indexes over weighted `tsvector` expressions:

- documents: title, summary, content, tags;
- knowledge entries: title, summary, body markdown, slug.

Queries are bound as parameters into `websearch_to_tsquery('english', @query)`.
That parser accepts normal user text, quoted phrases, punctuation, and stopword
heavy input without exposing raw `tsquery` syntax to callers. Empty or whitespace
queries still short-circuit in application code.

Snippets now come from `ts_headline` on the matched body text. Legacy SQLite
FTS5 tests keep `snippet(...)` and its virtual-table `rank` ordering. Postgres ranks with
`ts_rank_cd`, so larger rank values sort first; SQLite FTS5 rank keeps its
existing smaller-is-better ordering. Exact rank values and snippet boundaries are
therefore provider-specific, while the API contract remains stable: matching
results include title/summary metadata, a highlighted snippet, and a numeric
rank suitable only for ordering within one provider response.
