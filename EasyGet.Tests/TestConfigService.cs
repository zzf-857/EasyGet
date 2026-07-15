using EasyGet.Services;

namespace EasyGet.Tests;

internal sealed class TestConfigService : ConfigService
{
    public TestConfigService()
        : base(Path.Combine(
            Path.GetTempPath(),
            "EasyGetTests",
            Guid.NewGuid().ToString("N"),
            "config"))
    {
    }
}
