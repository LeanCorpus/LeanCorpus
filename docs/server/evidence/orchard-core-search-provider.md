# Orchard Core provider performance run

The paired explicit run compared the Orchard Lucene and LeanCorpus providers on one local host. It used 2,000 published Orchard content items containing indexed `Title` and `Body` fields. The complete raw record is [orchard-core-search-provider-runs.json](orchard-core-search-provider-runs.json).

| Operation | Lucene median / p95 | LeanCorpus median / p95 |
| --- | ---: | ---: |
| Generic search | 1.13 / 10.22 ms | 26.82 / 76.39 ms |
| Update through indexing and visibility search | 112.42 / 386.67 ms | 330.97 / 465.96 ms |
| Physical rebuild, median of three runs | 816.65 ms | 623.72 ms |

The initial build took 1,484.99 ms for Lucene and 1,573.78 ms for LeanCorpus. Search and update used one cold sample, three warm-up samples, and ten measured samples per provider. Search timing is collected inside `ISearchService.SearchAsync`, without loopback transport. Update timing sums the server-side content read, draft update and publish, Orchard task processing, and a target-provider visibility query. Each task is processed by both enabled providers, so update timings include both providers' work.

Build and rebuild timings use the same handler-built 2,000-document batch for each provider. They include physical replacement, Orchard content retrieval and field-handler mapping, one batch write, and cursor advancement. This explicit path bypasses per-record task-log replay and the provider-specific `ContentItemDocumentIndex` refresh path. The functional test covers normal task replay, refresh, cursor recovery, and content lifecycle behaviour. The performance corpus uses required `Title` and `Body` fields; integration tests cover additional mapped field types. The run did not flush operating-system caches.

Host: Debian GNU/Linux 13, .NET 11.0.0-preview.7.26381.103, Intel Xeon E3-1220 V2 at 3.10 GHz, 4 reported processors. The test ran on branch `alpha/sever-orchard` at source commit `36f3a3535404545591cf3de1a82eb24fefcf8f9d`, with intended source changes present in the worktree. The raw JSON captures exact timings, sample success, SDK, and repository status.
