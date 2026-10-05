namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal static class CsvSchemas
{
    public static readonly string[] Production =
    [
        "run_id", "platform_id", "launch", "cell_order", "observation", "warmup", "base_sha", "spike_sha",
        "dataset_id", "corpus_sha256", "representation", "durable", "lifecycle", "batch_docs", "baseline_docs",
        "indexing_concurrency", "flush_concurrency", "ram_buffer_mib", "merge_policy", "index_ms", "forced_flush_ms",
        "forced_flush_embedded_in_commit", "compound_pack_ms", "pack_embedded_in_commit", "metadata_prepare_ms",
        "durability_sync_ms", "post_commit_ms", "commit_call_ms", "operation_ms", "pack_member_count",
        "pack_input_bytes", "pack_output_bytes", "pack_source_read_bytes", "pack_temp_written_bytes",
        "pack_temp_explicit_persist_requests", "durability_candidate_files", "durability_candidate_bytes",
        "file_persist_requests", "file_persist_success", "file_persist_failed", "file_persist_elapsed_ms",
        "directory_persist_requests", "directory_persist_success", "directory_persist_unsupported",
        "directory_persist_failed", "directory_persist_elapsed_ms", "atomic_replace_count",
        "commit_marker_persist_requests", "files_created", "files_renamed", "files_deleted", "windows_retry_count",
        "windows_retry_delay_ms", "allocated_bytes_delta", "gen0_delta", "gen1_delta", "gen2_delta", "reopen_ok",
        "expected_doc_count", "actual_doc_count", "id_lookup_pass", "index_integrity_pass", "error"
    ];

    public static readonly string[] Cardinality =
    [
        "run_id", "platform_id", "launch", "cell_order", "observation", "warmup", "base_sha", "spike_sha",
        "object_count", "payload_bytes", "payload_sha256", "durable", "durability_sync_ms", "file_persist_requests",
        "file_persist_success", "file_persist_failed", "file_persist_elapsed_ms", "directory_persist_requests",
        "directory_persist_success", "directory_persist_unsupported", "directory_persist_failed",
        "directory_persist_elapsed_ms", "atomic_replace_count", "marker_bytes", "payload_reconstruction_pass", "error"
    ];

    public static readonly string[] Recovery =
    [
        "run_id", "platform_id", "representation", "checkpoint", "trial", "base_sha", "spike_sha",
        "termination_kind", "commit_returned", "reopen_ok", "recovered_doc_count", "recovered_generation_class",
        "id_lookup_pass", "index_integrity_pass", "missing_file_count", "truncated_file_count", "tmp_file_count",
        "directory_persist_outcome", "error"
    ];
}
