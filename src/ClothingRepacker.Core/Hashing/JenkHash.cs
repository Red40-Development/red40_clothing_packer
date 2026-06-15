namespace ClothingRepacker.Core.Hashing;

public static class JenkHash
{
    public static uint Hash(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        uint hash = 0;
        foreach (var ch in value.ToLowerInvariant())
        {
            hash += ch;
            hash += hash << 10;
            hash ^= hash >> 6;
        }

        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }
}
