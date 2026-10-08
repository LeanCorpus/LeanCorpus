using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.DependencyInjection;
using OrchardCore.DisplayManagement.Handlers;
using OrchardCore.DisplayManagement.Views;
using OrchardCore.Indexing;
using OrchardCore.Queries;
using Rowles.LeanCorpus.OrchardCore.Search.Indexing;
using Rowles.LeanCorpus.OrchardCore.Search.Queries;
using Rowles.LeanCorpus.OrchardCore.Search.Storage;

namespace Rowles.LeanCorpus.OrchardCore.Search.Drivers;

/// <summary>Edits and validates native LeanCorpus stored-query JSON.</summary>
public sealed class LeanCorpusQueryDisplayDriver : DisplayDriver<Query>
{
    private readonly IIndexProfileManager _profiles;
    private readonly LeanCorpusIndexPathResolver _paths;
    private readonly LeanCorpusSchemaStore _schemas;
    private readonly LeanCorpusQueryCompiler _compiler;

    public LeanCorpusQueryDisplayDriver(IServiceProvider services)
    {
        _profiles = services.GetRequiredService<IIndexProfileManager>();
        _paths = services.GetRequiredService<LeanCorpusIndexPathResolver>();
        _schemas = services.GetRequiredService<LeanCorpusSchemaStore>();
        _compiler = services.GetRequiredService<LeanCorpusQueryCompiler>();
    }

    public override async Task<IDisplayResult> EditAsync(Query query, BuildEditorContext context)
    {
        if (!IsLeanCorpus(query))
            return await base.EditAsync(query, context).ConfigureAwait(false);

        var profiles = (await _profiles.GetByProviderAsync("LeanCorpus").ConfigureAwait(false))
            .OrderBy(profile => profile.Name, StringComparer.Ordinal)
            .ToArray();
        string selected = ReadIndex(query.Schema);
        return Initialize<LeanCorpusQueryViewModel>("LeanCorpusQuery_Edit", model =>
        {
            model.IndexName = selected;
            model.Json = query.Schema ?? string.Empty;
            model.IndexOptions = profiles.Select(profile => new SelectListItem(profile.Name, profile.IndexName,
                string.Equals(profile.IndexName, selected, StringComparison.Ordinal))).ToArray();
        }).Location("Content:3");
    }

    public override async Task<IDisplayResult> UpdateAsync(Query query, UpdateEditorContext context)
    {
        if (!IsLeanCorpus(query))
            return await base.UpdateAsync(query, context).ConfigureAwait(false);

        var viewModel = new LeanCorpusQueryViewModel();
        await context.Updater.TryUpdateModelAsync(viewModel, Prefix).ConfigureAwait(false);
        var profiles = (await _profiles.GetByProviderAsync("LeanCorpus").ConfigureAwait(false)).ToArray();
        var selected = profiles.FirstOrDefault(profile => string.Equals(profile.IndexName, viewModel.IndexName, StringComparison.Ordinal));
        if (selected is null)
            context.Updater.ModelState.AddModelError(nameof(viewModel.IndexName), "Choose a LeanCorpus index profile.");

        string? savedJson = null;
        if (selected is not null)
        {
            try
            {
                JsonObject json = JsonNode.Parse(viewModel.Json) as JsonObject
                    ?? throw new InvalidDataException("The LeanCorpus query must be a JSON object.");
                json["index"] = selected.IndexName;
                savedJson = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                LeanCorpusSchemaManifest schema = await _schemas.ReadAsync(_paths.Resolve(selected.IndexFullName).SchemaPath).ConfigureAwait(false);
                _compiler.Compile(savedJson, schema);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or FormatException or OverflowException)
            {
                context.Updater.ModelState.AddModelError(nameof(viewModel.Json), exception.Message);
            }
        }

        if (!context.Updater.ModelState.IsValid)
            return await EditAsync(query, context).ConfigureAwait(false);

        query.Source = "LeanCorpus";
        query.Schema = savedJson!;
        return await EditAsync(query, context).ConfigureAwait(false);
    }

    private static bool IsLeanCorpus(Query query)
        => string.Equals(query.Source, "LeanCorpus", StringComparison.Ordinal);

    private static string ReadIndex(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("index", out var index) ? index.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}

public sealed class LeanCorpusQueryViewModel
{
    public string IndexName { get; set; } = string.Empty;
    public string Json { get; set; } = string.Empty;
    public SelectListItem[] IndexOptions { get; set; } = [];
}
