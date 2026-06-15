using System.Collections.ObjectModel;

namespace ClothingRepacker.Core;

public static class ClothingConstants
{
    public const int ComponentSlotCount = 12;
    public const int MissingComponent = 255;
    public const int DefaultMaxDrawablesPerComponent = 128;
    public const int DefaultMaxDrawablesPerProp = 255;

    public static readonly IReadOnlyDictionary<int, string> ComponentPrefixes =
        new ReadOnlyDictionary<int, string>(new Dictionary<int, string>
        {
            [0] = "head",
            [1] = "berd",
            [2] = "hair",
            [3] = "uppr",
            [4] = "lowr",
            [5] = "hand",
            [6] = "feet",
            [7] = "teef",
            [8] = "accs",
            [9] = "task",
            [10] = "decl",
            [11] = "jbib",
        });

    public static readonly IReadOnlyDictionary<int, string> PropPrefixes =
        new ReadOnlyDictionary<int, string>(new Dictionary<int, string>
        {
            [0] = "p_head",
            [1] = "p_eyes",
            [2] = "p_ears",
            [6] = "p_lwrist",
            [7] = "p_rwrist",
        });

    public static readonly IReadOnlyDictionary<int, string> AnchorNames =
        new ReadOnlyDictionary<int, string>(new Dictionary<int, string>
        {
            [0] = "ANCHOR_HEAD",
            [1] = "ANCHOR_EYES",
            [2] = "ANCHOR_EARS",
            [6] = "ANCHOR_LWRIST",
            [7] = "ANCHOR_RWRIST",
        });
}
