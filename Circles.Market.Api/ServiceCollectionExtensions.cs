namespace Circles.Market.Api;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection Decorate<TService, TDecorator>(this IServiceCollection services)
        where TService : class
        where TDecorator : class, TService
    {
        var descriptor = services.First(s => s.ServiceType == typeof(TService));
        var lifetime = descriptor.Lifetime;

        services.Remove(descriptor);

        object Factory(IServiceProvider sp)
        {
            TService original;

            bool hasInstance = descriptor.ImplementationInstance is not null;
            if (hasInstance)
            {
                original = (TService)descriptor.ImplementationInstance!;
            }
            else
            {
                bool hasFactory = descriptor.ImplementationFactory is not null;
                if (hasFactory)
                {
                    original = (TService)descriptor.ImplementationFactory!(sp)!;
                }
                else
                {
                    var implType = descriptor.ImplementationType
                                   ?? throw new InvalidOperationException($"Cannot decorate {typeof(TService)}: missing ImplementationType");
                    original = (TService)ActivatorUtilities.CreateInstance(sp, implType);
                }
            }

            return (TService)ActivatorUtilities.CreateInstance(sp, typeof(TDecorator), original);
        }

        services.Add(new ServiceDescriptor(typeof(TService), Factory, lifetime));
        return services;
    }
}