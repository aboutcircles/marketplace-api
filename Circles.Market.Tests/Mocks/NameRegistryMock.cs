using Circles.Profiles.Interfaces;
using Moq;

namespace Circles.Market.Tests.Mocks;

internal static class NameRegistryMock
{
    public static Mock<INameRegistry> WithProfileCid(string avatar, string cid)
    {
        var m = new Mock<INameRegistry>();
        m.Setup(r => r.GetProfileCidAsync(avatar, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cid);
        m.Setup(r => r.UpdateProfileCidAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("TX-MOCK");
        return m;
    }
}
