# Orchard Core search provider

`Rowles.LeanCorpus.OrchardCore.Search` adds an embedded LeanCorpus indexing provider, Orchard's generic search service, and a native LeanCorpus query source. It stores each tenant's indexes locally and does not call an external service.

The package is an optional Orchard Core module. It targets .NET 11 and is built against Orchard Core 3.0.1. The provider is released as `0.1.0-alpha.1`.

## Install and enable

Add the package to an Orchard Core application and enable the feature with ID `Rowles.LeanCorpus.OrchardCore.Search` (display name **LeanCorpus Search Provider**). The module registers a content indexing source named `LeanCorpus`, an `ISearchService` named `LeanCorpus`, and an Orchard query source named `LeanCorpus`.

In the Orchard admin, create a **Content** index profile and choose **LeanCorpus** as its provider. Select the content types to index and configure their field indexing settings. Build the profile after creating or changing its indexed content types and fields.

The provider maps Orchard text, Boolean, integer, number, date/time, geo-point, and vector entries to LeanCorpus fields. Text entries are analysed unless Orchard marks them as keyword fields. Complex entries are recorded as unsupported and are not indexed. A field's type and indexing representation must remain consistent within an index; incompatible later values fail indexing rather than changing the persisted schema.

Index data, schema manifests, and indexing cursors are kept below:

```text
<content-root>/App_Data/Sites/<tenant-key>/LeanCorpus/
    indexes/<index-key>/
    schemas/<index-key>.json
    state/<index-key>.json
```

Tenant and index keys are derived from the Orchard names using a readable prefix and hash, so raw names are not used as filesystem paths. Keep this tenant data with the rest of Orchard's persistent `App_Data` when backing up or moving a site.

## Configure generic site search

Edit the LeanCorpus index profile's provider settings:

- **Default analyser** is `standard` by default. `keyword` is also available.
- **Site-search fields** selects which indexed analysed-text fields participate in Orchard's generic search. Leaving the selection empty searches every indexed analysed-text field.

Changing the analyser after fields have been indexed requires a physical profile rebuild. Search fields must refer to fields present in the index schema and represented as analysed text. Keyword text fields remain available to native term queries, but are not part of generic analysed-text search.

Orchard's generic search service returns content item IDs, ordered by descending score with a stable ID tie-break. It caps each page at 100 results. Search text is limited to 4,096 characters and at most 64 distinct analysed tokens. The provider does not currently return highlights.

## Create a native LeanCorpus query

Create an Orchard query and select **LeanCorpus** as its source. Choose a LeanCorpus index profile and enter a bounded native query object. The editor validates the query against that index's persisted schema and adds the selected index name when it saves the query.

For example, this matches analysed text in one field:

```json
{
  "query": {
    "type": "match",
    "field": "Article.Title",
    "value": "orchard search"
  },
  "skip": 0,
  "take": 20
}
```

Supported clause types are `match`, `term`, `range`, `exists`, `isNull`, `and`, `or`, `not`, `geoDistance`, and `vectorKnn`. Field names and supported operations are checked against the index schema. Boolean clauses accept 1 to 32 child clauses. Queries are limited to 16,384 JSON characters, nesting depth 16, and a `take` value of at most 100; `skip` is limited to 1,000,000. Vector queries must match the indexed dimensions, and their `topK` is limited to 100.

## Functional and performance evidence

The paired provider run used Orchard Core 3.0.1 on Debian GNU/Linux 13 with .NET 11 preview 7, an Intel Xeon E3-1220 V2 at 3.10 GHz, and a 2,000-item published content corpus. It compared the Orchard Lucene and LeanCorpus profiles in the same host. Each search and update phase had one cold sample, three warm-up samples, and ten measured samples per provider. The reported search values are the median and p95 of those ten measured samples.

| Operation | Lucene median / p95 | LeanCorpus median / p95 |
| --- | ---: | ---: |
| Generic search | 1.13 / 10.22 ms | 26.82 / 76.39 ms |
| Update through indexing and visibility search | 112.42 / 386.67 ms | 330.97 / 465.96 ms |
| Physical rebuild, median of three runs | 816.65 ms | 623.72 ms |

The initial build took 1,484.99 ms for Lucene and 1,573.78 ms for LeanCorpus. Search timing is measured inside the host and excludes loopback transport. Update timing sums the server-side content read, draft update and publish, indexing-task processing, and a visibility query. Each update task is processed by both enabled provider profiles, so that row includes work by both providers.

The explicit build and rebuild measurement sends both providers the same 2,000 handler-built documents in one batch. It includes physical replacement, Orchard content retrieval and field-handler mapping, the batch write, and cursor advancement. It bypasses per-record task-log replay and the provider-specific content-index refresh path; the functional lifecycle tests cover task replay, normal content refresh, reset, update, delete, and cursor recovery. The corpus uses the required Title and Body fields, while provider integration tests exercise wider field mappings. Operating-system caches were not flushed. These are results from one local run, not a cross-machine performance guarantee.

The complete measurement record, including sample data and host provenance, is in [the raw run report](evidence/orchard-core-search-provider-runs.json). Run the explicit comparison with:

```bash
DOTNET_USE_POLLING_FILE_WATCHER=1 \
TESTINGPLATFORM_PIPE_DIRECTORY=/tmp/lc \
LEANCORE_ORCHARD_PERF_OUTPUT="$PWD/docs/server/evidence/orchard-core-search-provider-runs.json" \
./devops test -Suite server-orchard-performance -Area Performance -Explicit
```

The functional browser test installs a test Orchard site with paired Lucene and LeanCorpus profiles, then checks search parity through publish, update, reset, deletion, and index-queue processing. The provider unit and integration suite covers schema mapping, persistence, replacement, cursor safety, and failure recovery.
