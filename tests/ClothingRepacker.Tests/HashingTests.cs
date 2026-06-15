using ClothingRepacker.Core.Hashing;

namespace ClothingRepacker.Tests;

public class HashingTests
{
    [Fact]
    public void HashesKnownCollections()
    {
        Assert.Equal(0x7F16400Du, JenkHash.Hash("red40_clothes"));
        Assert.Equal(0x6BE06652u, JenkHash.Hash("mp_f_accessoriesx2"));
    }
}
