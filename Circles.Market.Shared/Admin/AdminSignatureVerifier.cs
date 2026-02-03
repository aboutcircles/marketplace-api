using Circles.Profiles.Interfaces;
using Circles.Profiles.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethereum.Web3;

namespace Circles.Market.Shared.Admin;

public static class AdminSignatureVerifier
{
    public static void AddAdminSignatureVerifier(this IServiceCollection services)
    {
        var chainRpcUrl = Environment.GetEnvironmentVariable("RPC")
                          ?? throw new Exception("The RPC env variable is not set.");
        services.AddSingleton<IChainApi>(_ =>
            new EthereumChainApi(new Web3(chainRpcUrl), Helpers.DefaultChainId));
        services.AddSingleton<DefaultSignatureVerifier>(sp =>
            new DefaultSignatureVerifier(
                sp.GetRequiredService<IChainApi>(),
                sp.GetRequiredService<ILogger<DefaultSignatureVerifier>>()));
        services.AddSingleton<ISignatureVerifier>(sp => sp.GetRequiredService<DefaultSignatureVerifier>());
        services.AddSingleton<ISafeBytesVerifier>(sp => sp.GetRequiredService<DefaultSignatureVerifier>());
    }
}
