using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Components;

public sealed record ImagePickerSelection(
    string RegistryUrl,
    string ImageName,
    string Tag,
    RegistryType RegistryType);
