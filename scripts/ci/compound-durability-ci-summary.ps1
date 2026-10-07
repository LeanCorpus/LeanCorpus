param(
    [Parameter(Mandatory = $true)]
    [string] $Root,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedMeasurementSha,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedDatasetSha
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = [IO.Path]::GetFullPath($Root)
$platformIds = @('linux-ext4', 'windows-ntfs')
$lifecycles = @('fresh', 'reopened-first', 'reopened-steady')
$launches = @(1, 2, 3, 4, 5)

function Get-Median {
    param([double[]] $Values)

    if ($null -eq $Values -or $Values.Count -eq 0) {
        return $null
    }

    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return [double]$sorted[$middle]
    }

    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Get-Double {
    param([object] $Value)

    $parsed = 0.0
    if (-not [double]::TryParse(
        [string]$Value,
        [Globalization.NumberStyles]::Float,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref]$parsed)) {
        throw "Could not parse floating-point value '$Value'."
    }

    return $parsed
}

function Get-Int64 {
    param([object] $Value)

    $parsed = 0L
    if (-not [long]::TryParse(
        [string]$Value,
        [Globalization.NumberStyles]::Integer,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref]$parsed)) {
        throw "Could not parse integer value '$Value'."
    }

    return $parsed
}

function Format-Milliseconds {
    param([object] $Value)

    if ($null -eq $Value) {
        return 'n/a'
    }

    return ([double]$Value).ToString('0.###', [Globalization.CultureInfo]::InvariantCulture)
}

function Get-MetricMedian {
    param(
        [object[]] $Rows,
        [string] $Column
    )

    $values = @($Rows | ForEach-Object { Get-Double $_.$Column })
    return Get-Median $values
}

function Get-StructuralMedian {
    param(
        [object[]] $Rows,
        [string] $LooseColumn,
        [string] $CompoundColumn
    )

    $values = @(
        $Rows | ForEach-Object {
            (Get-Double $_.$LooseColumn) - (Get-Double $_.$CompoundColumn)
        }
    )
    return Get-Median $values
}

function Test-Positive {
    param([object] $Value)

    if ($null -eq $Value) {
        return $false
    }

    return [double]$Value -gt 0
}

$platformResults = @()

foreach ($platformId in $platformIds) {
    $platformRoot = Join-Path $Root $platformId
    if (-not (Test-Path $platformRoot)) {
        throw "Missing platform handoff directory '$platformRoot'."
    }

    $identity = Get-Content (Join-Path $platformRoot 'dataset-identity.json') -Raw | ConvertFrom-Json
    $source = Get-Content (Join-Path $platformRoot 'source-and-assembly-hashes.json') -Raw | ConvertFrom-Json
    $runner = Get-Content (Join-Path $platformRoot 'ci-runner-metadata.json') -Raw | ConvertFrom-Json
    $production = @(Import-Csv (Join-Path $platformRoot 'production-trials.csv'))
    $pairs = @(Import-Csv (Join-Path $platformRoot 'production-pairs.csv'))

    $measured = @($production | Where-Object warmup -eq 'false')
    $warmups = @($production | Where-Object warmup -eq 'true')
    $measuredPairs = @($pairs | Where-Object observation -ne '0')
    $validMeasuredPairs = @($measuredPairs | Where-Object pair_status -eq 'valid')

    $measuredErrors = @($measured | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_.error)
    })

    $reopenFailures = @($measured | Where-Object reopen_ok -ne 'true')
    $lookupFailures = @($measured | Where-Object id_lookup_pass -ne 'true')
    $integrityFailures = @($measured | Where-Object index_integrity_pass -ne 'true')
    $topologyFailures = @($measuredPairs | Where-Object pair_status -eq 'topology_mismatch')
    $failedPairs = @($measuredPairs | Where-Object pair_status -ne 'valid')

    $steadyRows = @($production | Where-Object lifecycle -eq 'reopened-steady')
    $invalidSteadyPriming = @(
        $steadyRows | Where-Object {
            if ($_.priming_open_durable -ne 'true' -or
                $_.priming_durable_baseline_pass -ne 'true') {
                return $true
            }

            $expected = Get-Int64 $_.priming_expected_inherited_file_count
            $fileRequests = Get-Int64 $_.priming_file_persist_requests
            $directoryRequests = Get-Int64 $_.priming_directory_persist_requests
            return $expected -le 0 -or $fileRequests -lt $expected -or $directoryRequests -le 0
        }
    )

    $tempPersistFailures = @($production | Where-Object {
        (Get-Int64 $_.pack_temp_explicit_persist_requests) -ne 0
    })

    $durablePairs = @($validMeasuredPairs | Where-Object durable -eq 'true')
    $candidateFileSaving = Get-StructuralMedian `
        -Rows $durablePairs `
        -LooseColumn 'loose_durability_candidate_files' `
        -CompoundColumn 'compound_durability_candidate_files'
    $fileRequestSaving = Get-StructuralMedian `
        -Rows $durablePairs `
        -LooseColumn 'loose_file_persist_requests' `
        -CompoundColumn 'compound_file_persist_requests'

    $lifecycleResults = @()
    $stableSupportingLifecycles = @()

    foreach ($lifecycle in $lifecycles) {
        $lifecycleRows = @($durablePairs | Where-Object lifecycle -eq $lifecycle)
        $launchSets = @(
            [pscustomobject]@{ name = 'all'; omitted_launch = $null; launches = $launches }
        )

        foreach ($omitted in $launches) {
            $remaining = @($launches | Where-Object { $_ -ne $omitted })
            $launchSets += [pscustomobject]@{
                name = "omit-$omitted"
                omitted_launch = $omitted
                launches = $remaining
            }
        }

        $sensitivity = @()
        foreach ($launchSet in $launchSets) {
            $rows = @($lifecycleRows | Where-Object {
                $launchSet.launches -contains [int]$_.launch
            })

            $durabilityMedian = Get-MetricMedian -Rows $rows -Column 'durability_saving_ms'
            $operationMedian = Get-MetricMedian -Rows $rows -Column 'operation_saving_ms'

            $sensitivity += [pscustomobject]@{
                launch_set = $launchSet.name
                omitted_launch = $launchSet.omitted_launch
                n = $rows.Count
                durability_saving_median_ms = $durabilityMedian
                operation_saving_median_ms = $operationMedian
                both_positive = (Test-Positive $durabilityMedian) -and (Test-Positive $operationMedian)
            }
        }

        $stable = @($sensitivity | Where-Object { -not $_.both_positive }).Count -eq 0
        if ($stable) {
            $stableSupportingLifecycles += $lifecycle
        }

        $all = $sensitivity | Where-Object launch_set -eq 'all' | Select-Object -First 1
        $lifecycleResults += [pscustomobject]@{
            lifecycle = $lifecycle
            measured_pair_count = $lifecycleRows.Count
            durability_saving_median_ms = $all.durability_saving_median_ms
            operation_saving_median_ms = $all.operation_saving_median_ms
            stable_positive_full_and_all_loo = $stable
            sensitivity = $sensitivity
        }
    }

    $sourceMatches = [string]$source.experiment_sha -eq $ExpectedMeasurementSha
    $datasetMatches = [string]$identity.canonical_content_sha256 -eq $ExpectedDatasetSha
    $runnerPinMatches = [string]$runner.measurement_sha -eq $ExpectedMeasurementSha

    $complete = (
        $production.Count -eq 360 -and
        $measured.Count -eq 300 -and
        $warmups.Count -eq 60 -and
        $pairs.Count -eq 180 -and
        $measuredPairs.Count -eq 150 -and
        $validMeasuredPairs.Count -eq 150
    )

    $correct = (
        $measuredErrors.Count -eq 0 -and
        $reopenFailures.Count -eq 0 -and
        $lookupFailures.Count -eq 0 -and
        $integrityFailures.Count -eq 0 -and
        $topologyFailures.Count -eq 0 -and
        $failedPairs.Count -eq 0 -and
        $invalidSteadyPriming.Count -eq 0 -and
        $tempPersistFailures.Count -eq 0
    )

    $structuralReduction = (
        (Test-Positive $candidateFileSaving) -and
        (Test-Positive $fileRequestSaving)
    )

    $platformReady = (
        $sourceMatches -and
        $datasetMatches -and
        $runnerPinMatches -and
        $complete -and
        $correct
    )

    $platformSupportsDirection = (
        $platformReady -and
        $structuralReduction -and
        $stableSupportingLifecycles.Count -gt 0
    )

    $platformResults += [pscustomobject]@{
        platform_id = $platformId
        runner = $runner
        experiment_sha = [string]$source.experiment_sha
        dataset_sha256 = [string]$identity.canonical_content_sha256
        source_matches_expected_measurement_sha = $sourceMatches
        dataset_matches_expected_sha = $datasetMatches
        runner_metadata_matches_expected_measurement_sha = $runnerPinMatches
        production_rows = $production.Count
        measured_rows = $measured.Count
        warmup_rows = $warmups.Count
        pair_rows = $pairs.Count
        measured_pairs = $measuredPairs.Count
        valid_measured_pairs = $validMeasuredPairs.Count
        measured_errors = $measuredErrors.Count
        reopen_failures = $reopenFailures.Count
        id_lookup_failures = $lookupFailures.Count
        integrity_failures = $integrityFailures.Count
        topology_mismatches = $topologyFailures.Count
        failed_pairs = $failedPairs.Count
        invalid_steady_priming_rows = $invalidSteadyPriming.Count
        compound_temp_persist_failures = $tempPersistFailures.Count
        median_candidate_file_saving = $candidateFileSaving
        median_file_persist_request_saving = $fileRequestSaving
        structural_reduction = $structuralReduction
        stable_supporting_lifecycles = $stableSupportingLifecycles
        complete_and_correct = $platformReady
        supports_spike_1b_direction = $platformSupportsDirection
        lifecycle_results = $lifecycleResults
    }
}

$bothReady = @($platformResults | Where-Object { -not $_.complete_and_correct }).Count -eq 0
$supportingPlatforms = @($platformResults | Where-Object supports_spike_1b_direction).Count

$outcome = if (-not $bothReady) {
    'invalid_or_incomplete'
}
elseif ($supportingPlatforms -eq 2) {
    'replicates_premise_supported_direction'
}
elseif ($supportingPlatforms -eq 1) {
    'mixed_platform_replication'
}
else {
    'does_not_replicate_supported_direction'
}

$summary = [ordered]@{
    schema = 'compound-durability-ci-replication-v1'
    generated_utc = [DateTime]::UtcNow.ToString('O')
    expected_measurement_sha = $ExpectedMeasurementSha
    expected_dataset_sha256 = $ExpectedDatasetSha
    replication_outcome = $outcome
    platform_results = $platformResults
}

$jsonPath = Join-Path $Root 'ci-replication-summary.json'
$summary | ConvertTo-Json -Depth 12 |
    Set-Content $jsonPath -Encoding utf8NoBOM

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Compound durability GitHub-hosted CI replication')
$lines.Add('')
$lines.Add("Measurement source: ``$ExpectedMeasurementSha``")
$lines.Add('')
$lines.Add("Locked dataset SHA-256: ``$ExpectedDatasetSha``")
$lines.Add('')
$lines.Add("Replication outcome: **$outcome**")
$lines.Add('')
$lines.Add('This is a production-matrix replication on GitHub-hosted VMs. It is additional robustness evidence and does not replace the publication-preferred Spike 1B run.')
$lines.Add('')
$lines.Add('## Runner and evidence validity')
$lines.Add('')
$lines.Add('| Platform | Runner image | Production rows | Measured pairs | Errors | Correctness failures | Steady priming failures | Dataset/source pin |')
$lines.Add('|---|---|---:|---:|---:|---:|---:|---|')

foreach ($platform in $platformResults) {
    $correctnessFailures = (
        $platform.reopen_failures +
        $platform.id_lookup_failures +
        $platform.integrity_failures +
        $platform.topology_mismatches +
        $platform.failed_pairs
    )
    $pin = $platform.source_matches_expected_measurement_sha -and
        $platform.dataset_matches_expected_sha -and
        $platform.runner_metadata_matches_expected_measurement_sha

    $lines.Add(
        "| $($platform.platform_id) | $($platform.runner.image_os) / $($platform.runner.image_version) | " +
        "$($platform.production_rows) | $($platform.valid_measured_pairs) | $($platform.measured_errors) | " +
        "$correctnessFailures | $($platform.invalid_steady_priming_rows) | $pin |"
    )
}

$lines.Add('')
$lines.Add('## Paired production medians')
$lines.Add('')
$lines.Add('| Platform | Lifecycle | Durability saving median ms | End-to-end saving median ms | Positive under full + every LOO |')
$lines.Add('|---|---|---:|---:|---|')

foreach ($platform in $platformResults) {
    foreach ($lifecycle in $platform.lifecycle_results) {
        $lines.Add(
            "| $($platform.platform_id) | $($lifecycle.lifecycle) | " +
            "$(Format-Milliseconds $lifecycle.durability_saving_median_ms) | " +
            "$(Format-Milliseconds $lifecycle.operation_saving_median_ms) | " +
            "$($lifecycle.stable_positive_full_and_all_loo) |"
        )
    }
}

$lines.Add('')
$lines.Add('## Structural reduction')
$lines.Add('')
$lines.Add('| Platform | Median candidate-file saving | Median file-persist-request saving | Stable supporting lifecycle(s) | Supports Spike 1B direction |')
$lines.Add('|---|---:|---:|---|---|')

foreach ($platform in $platformResults) {
    $stable = if ($platform.stable_supporting_lifecycles.Count -eq 0) {
        'none'
    }
    else {
        $platform.stable_supporting_lifecycles -join ', '
    }

    $lines.Add(
        "| $($platform.platform_id) | $($platform.median_candidate_file_saving) | " +
        "$($platform.median_file_persist_request_saving) | $stable | $($platform.supports_spike_1b_direction) |"
    )
}

$lines.Add('')
$lines.Add('## Leave-one-launch-out detail')
$lines.Add('')

foreach ($platform in $platformResults) {
    $lines.Add("### $($platform.platform_id)")
    $lines.Add('')
    $lines.Add('| Lifecycle | Launch set | n | Durability saving median ms | End-to-end saving median ms | Both positive |')
    $lines.Add('|---|---|---:|---:|---:|---|')

    foreach ($lifecycle in $platform.lifecycle_results) {
        foreach ($entry in $lifecycle.sensitivity) {
            $lines.Add(
                "| $($lifecycle.lifecycle) | $($entry.launch_set) | $($entry.n) | " +
                "$(Format-Milliseconds $entry.durability_saving_median_ms) | " +
                "$(Format-Milliseconds $entry.operation_saving_median_ms) | $($entry.both_positive) |"
            )
        }
    }

    $lines.Add('')
}

$markdownPath = Join-Path $Root 'ci-replication-summary.md'
$lines | Set-Content $markdownPath -Encoding utf8NoBOM

$manifest = Join-Path $Root 'ci-handoff-sha256sums.txt'
Get-ChildItem $Root -File -Recurse |
    Where-Object FullName -ne $manifest |
    Sort-Object FullName |
    ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
        "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $relative"
    } |
    Set-Content $manifest -Encoding utf8NoBOM

Write-Host "replication_outcome=$outcome"
Write-Host "summary_markdown=$markdownPath"
Write-Host "summary_json=$jsonPath"
Write-Host "manifest=$manifest"
