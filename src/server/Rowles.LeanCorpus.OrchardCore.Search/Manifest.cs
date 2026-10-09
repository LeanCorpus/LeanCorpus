using OrchardCore.Modules.Manifest;

[assembly: Module(
    Name = "LeanCorpus Orchard Core Search",
    Author = "LeanCorpus",
    Version = "0.1.0",
    Description = "Embedded LeanCorpus indexing and search provider for Orchard Core.")]

[assembly: Feature(
    Id = "Rowles.LeanCorpus.OrchardCore.Search",
    Name = "LeanCorpus Search Provider",
    Category = "Search",
    Description = "Adds the LeanCorpus index provider and native query source.")]
