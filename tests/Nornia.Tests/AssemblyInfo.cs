using Xunit;

// View-model tests also touch Application resources and process-wide theme/font services.
// Running them alongside the shared STA host can mutate dictionaries while BAML is loading.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
