using ClothingRepacker.CodeWalker;
using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Services;

namespace ClothingRepacker.Gui.Services;

public sealed class RepackerServiceFactory
{
    public RepackerService Create()
        => new(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
}
