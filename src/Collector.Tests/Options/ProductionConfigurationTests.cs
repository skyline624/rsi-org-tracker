using Collector.Options;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Collector.Tests.Options;

public class ProductionConfigurationTests
{
    [Fact]
    public void ShippedProductionSettingsPassCollectorValidation()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("collector.production-appsettings.json", optional: false)
            .Build();
        var options = configuration.GetSection("Collector").Get<CollectorOptions>();
        Assert.NotNull(options);

        var result = new CollectorOptionsValidator().Validate(null, options);

        Assert.True(result.Succeeded, result.FailureMessage);
    }
}
