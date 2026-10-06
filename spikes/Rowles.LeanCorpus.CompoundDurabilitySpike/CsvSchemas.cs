namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal static class CsvSchemas
{
    public static readonly string[] Production =
    [
        "run_id", "platform_id", "experiment_sha", "base_sha", "launch", "cell_order", "observation", "warmup",
        "dataset_id", "corpus_sha256", "representation", "durable", "lifecycle", "batch_docs", "baseline_docs",
        "indexing_concurrency", "flush_concurrency", "ram_buffer_mib", "max_buffered_docs", "merge_policy", "index_ms", "forced_flush_ms",
        "forced_flush_embedded_in_commit", "compound_pack_ms", "pack_embedded_in_commit", "metadata_prepare_ms",
        "durability_sync_ms", "post_commit_ms", "commit_call_ms", "operation_ms", "pack_member_count",
        "pack_member_size_vector_bytes", "pre_measure_segment_count", "pre_measure_segment_doc_vector",
        "post_measure_segment_count", "post_measure_segment_doc_vector", "priming_commit_generation_before",
        "priming_commit_generation_after", "priming_file_persist_requests", "priming_directory_persist_requests",
        "priming_durable_baseline_pass",
        "pack_input_bytes", "pack_output_bytes", "pack_source_read_bytes", "pack_temp_written_bytes",
        "pack_temp_explicit_persist_requests", "durability_candidate_files", "durability_candidate_bytes",
        "file_persist_requests", "file_persist_success", "file_persist_failed", "file_persist_elapsed_ms",
        "directory_persist_requests", "directory_persist_success", "directory_persist_unsupported",
        "directory_persist_failed", "directory_persist_elapsed_ms", "atomic_replace_count",
        "commit_marker_persist_requests", "files_created", "files_renamed", "files_deleted", "windows_retry_count",
        "windows_retry_delay_ms", "allocated_bytes_delta", "gen0_delta", "gen1_delta", "gen2_delta", "reopen_ok",
        "expected_doc_count", "actual_doc_count", "id_lookup_pass", "index_integrity_pass", "error"
    ];

    public static readonly string[] ProductionPairs =
    [
        "platform_id", "launch", "observation", "lifecycle", "durable", "pair_status", "exclusion_reason",
        "loose_pre_vector", "compound_pre_vector", "loose_post_vector", "compound_post_vector",
        "loose_durability_sync_ms", "compound_durability_sync_ms", "durability_saving_ms",
        "loose_commit_call_ms", "compound_commit_call_ms",
        "loose_operation_ms", "compound_operation_ms", "operation_saving_ms",
        "loose_durability_candidate_files", "compound_durability_candidate_files",
        "loose_file_persist_requests", "compound_file_persist_requests"
    ];

    public static readonly string[] Cardinality =
    [
        "run_id", "platform_id", "experiment_sha", "base_sha", "launch", "cell_order", "observation", "warmup",
        "object_count", "payload_bytes", "payload_sha256", "partition_mode", "partition_vector_bytes",
        "partition_vector_sha256", "durability_sync_ms", "file_persist_requests",
        "file_persist_success", "file_persist_failed", "file_persist_elapsed_ms", "directory_persist_requests",
        "directory_persist_success", "directory_persist_unsupported", "directory_persist_failed",
        "directory_persist_elapsed_ms", "atomic_replace_count", "marker_bytes", "payload_reconstruction_pass", "error"
    ];

    public static readonly string[] CardinalityPartitions =
    [
        "payload_bytes", "object_count", "partition_mode", "source_workload", "source_member_count",
        "source_member_sizes_bytes", "partition_vector_bytes", "partition_vector_sha256", "partition_total_bytes"
    ];

    public static readonly string[] PairedAnalysis =
    [
        "platform_id", "lifecycle", "launch_set", "metric", "sample_count", "median", "q1", "q3", "iqr",
        "bootstrap_95ci_low", "bootstrap_95ci_high", "min", "max", "launch_level_medians_json"
    ];

    public static readonly string[] Recovery =
    [
        "run_id", "platform_id", "representation", "checkpoint", "trial", "base_sha", "spike_sha",
        "termination_kind", "commit_returned", "reopen_ok", "recovered_doc_count", "recovered_generation_class",
        "id_lookup_pass", "index_integrity_pass", "missing_file_count", "truncated_file_count", "tmp_file_count",
        "directory_persist_outcome", "error"
    ];
}
