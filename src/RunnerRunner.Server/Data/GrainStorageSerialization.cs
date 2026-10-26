using Newtonsoft.Json;
using Orleans.Serialization;

namespace RunnerRunner.Server.Data;

internal static class GrainStorageSerialization
{
    public static void Configure(OrleansJsonSerializerOptions options)
    {
        // An omitted false/zero otherwise reloads as a property's nonzero initializer.
        // Keep the existing JSON format, converters and type binder for stored grains.
        options.JsonSerializerSettings.DefaultValueHandling = DefaultValueHandling.Include;
    }
}
