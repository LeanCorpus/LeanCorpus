using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.DependencyInjection;
using OrchardCore.DisplayManagement.Handlers;
using OrchardCore.DisplayManagement.ModelBinding;
using OrchardCore.DisplayManagement.Views;
using OrchardCore.Entities;
using OrchardCore.Indexing.Models;
using Rowles.LeanCorpus.OrchardCore.Search.Indexing;
using Rowles.LeanCorpus.OrchardCore.Search.Models;
using Rowles.LeanCorpus.OrchardCore.Search.Storage;

namespace Rowles.LeanCorpus.OrchardCore.Search.Drivers;

/// <summary>Edits the LeanCorpus analyser and generic site-search fields for an index profile.</summary>
public sealed class LeanCorpusIndexProfileDisplayDriver : DisplayDriver<IndexProfile>
{
    private readonly LeanCorpusIndexPathResolver _paths;
    private readonly LeanCorpusSchemaStore _schemas;

    public LeanCorpusIndexProfileDisplayDriver(IServiceProvider services)
    {
        _paths = services.GetRequiredService<LeanCorpusIndexPathResolver>();
        _schemas = services.GetRequiredService<LeanCorpusSchemaStore>();
    }

    public override async Task<IDisplayResult> EditAsync(IndexProfile profile, BuildEditorContext context)
    {
        if (!IsLeanCorpus(profile))
            return await base.EditAsync(profile, context).ConfigureAwait(false);

        LeanCorpusIndexMetadata metadata = profile.GetLeanCorpusMetadata();
        string[] fields = [];
        try
        {
            LeanCorpusSchemaManifest schema = await _schemas.ReadAsync(_paths.Resolve(profile.IndexFullName).SchemaPath).ConfigureAwait(false);
            fields = schema.Fields.Values
                .Where(field => field.Indexed && field.Representation == "analysed-text")
                .Select(field => field.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (FileNotFoundException)
        {
            // Newly-created profiles have no observed fields yet. An empty selection derives fields from the schema.
        }
        catch (InvalidDataException)
        {
            // The editor remains usable so an administrator can repair provider settings before rebuilding.
        }

        var viewModel = new LeanCorpusIndexProfileViewModel
        {
            DefaultAnalyser = metadata.DefaultAnalyser,
            SearchFields = metadata.SearchFields,
            SearchFieldOptions = fields.Select(field => new SelectListItem(field, field,
                metadata.SearchFields.Contains(field, StringComparer.Ordinal))).ToArray(),
        };
        return Initialize<LeanCorpusIndexProfileViewModel>("LeanCorpusIndexProfile_Edit", model =>
        {
            model.DefaultAnalyser = viewModel.DefaultAnalyser;
            model.SearchFields = viewModel.SearchFields;
            model.SearchFieldOptions = viewModel.SearchFieldOptions;
        }).Location("Content:3");
    }

    public override async Task<IDisplayResult> UpdateAsync(IndexProfile profile, UpdateEditorContext context)
    {
        if (!IsLeanCorpus(profile))
            return await base.UpdateAsync(profile, context).ConfigureAwait(false);

        var viewModel = new LeanCorpusIndexProfileViewModel();
        await context.Updater.TryUpdateModelAsync(viewModel, Prefix).ConfigureAwait(false);
        if (viewModel.DefaultAnalyser is not ("standard" or "keyword"))
            context.Updater.ModelState.AddModelError(nameof(viewModel.DefaultAnalyser), "Choose Standard or Keyword.");

        LeanCorpusSchemaManifest schema;
        try
        {
            schema = await _schemas.ReadAsync(_paths.Resolve(profile.IndexFullName).SchemaPath).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.Updater.ModelState.AddModelError(nameof(viewModel.SearchFields), "The LeanCorpus schema is unavailable. Rebuild the index before configuring search fields.");
            return await EditAsync(profile, context).ConfigureAwait(false);
        }

        var available = schema.Fields.Values
            .Where(field => field.Indexed && field.Representation == "analysed-text")
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (viewModel.SearchFields.Any(field => !available.Contains(field)))
            context.Updater.ModelState.AddModelError(nameof(viewModel.SearchFields), "Select only indexed analysed text fields.");

        if (!context.Updater.ModelState.IsValid)
            return await EditAsync(profile, context).ConfigureAwait(false);

        profile.Put(LeanCorpusIndexProfileMetadataExtensions.EntityName, new LeanCorpusIndexMetadata
        {
            DefaultAnalyser = viewModel.DefaultAnalyser,
            SearchFields = viewModel.SearchFields.Distinct(StringComparer.Ordinal).ToArray(),
        });

        return await EditAsync(profile, context).ConfigureAwait(false);
    }

    private static bool IsLeanCorpus(IndexProfile profile)
        => string.Equals(profile.ProviderName, "LeanCorpus", StringComparison.Ordinal);
}

public sealed class LeanCorpusIndexProfileViewModel
{
    public string DefaultAnalyser { get; set; } = "standard";
    public string[] SearchFields { get; set; } = [];
    public SelectListItem[] SearchFieldOptions { get; set; } = [];
}
