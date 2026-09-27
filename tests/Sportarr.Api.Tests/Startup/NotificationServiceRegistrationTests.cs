using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Sportarr.Api.Services;
using Sportarr.Api.Services.Interfaces;
using Sportarr.Api.Startup;

namespace Sportarr.Api.Tests.Startup;

public class NotificationServiceRegistrationTests
{
    [Fact]
    public void ConcreteAndInterfaceRegistrationsShareOneScopedInstance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSportarrHttpClients();
        services.AddSportarrCoreServices();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var concrete = scope.ServiceProvider.GetRequiredService<NotificationService>();
        var abstraction = scope.ServiceProvider.GetRequiredService<INotificationService>();

        abstraction.Should().BeSameAs(concrete);
    }
}
