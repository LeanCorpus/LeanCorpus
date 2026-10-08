using OrchardCore.Modules.Manifest;

[assembly: Module(
    Name = "LeanCorpus Search Test Host",
    Author = "LeanCorpus",
    Version = "0.1.0",
    Description = "Deterministic content and profiles for the LeanCorpus Orchard provider test host.")]

[assembly: Feature(
    Id = "Rowles.LeanCorpus.OrchardCore.Search.TestHostModule",
    Name = "LeanCorpus Search Test Host",
    Category = "Testing",
    Description = "Provides the deterministic LeanCorpus provider test recipe.")]
