using MutaMaxxing.Core;

namespace MutaMaxxing.Core.Tests;

[TestClass]
public sealed class MateriaTransmutationResultTests
{
    [TestMethod]
    public void IdentifiesOneNewMateriaAfterAccountingForConsumedInputs()
    {
        var received = MateriaTransmutationResult.IdentifyReceivedMateria(
            [1001, 1002, 1003],
            new Dictionary<uint, uint> { [1001] = 7, [1002] = 5, [1003] = 0 },
            new Dictionary<uint, uint> { [1001] = 10, [1002] = 4, [1003] = 0 },
            new Dictionary<uint, uint> { [1001] = 3 });

        Assert.AreEqual(1002u, received);
    }

    [TestMethod]
    public void LeavesUnknownResultsUnidentifiedWhenTheDeltaIsAmbiguous()
    {
        var current = new Dictionary<uint, uint> { [1001] = 7, [1002] = 5, [1003] = 1 };
        var baseline = new Dictionary<uint, uint> { [1001] = 10, [1002] = 4, [1003] = 0 };
        var inputs = new Dictionary<uint, uint> { [1001] = 3 };

        Assert.IsNull(MateriaTransmutationResult.IdentifyReceivedMateria([1001, 1002, 1003], current, baseline, inputs));
        Assert.IsNull(MateriaTransmutationResult.IdentifyReceivedMateria([1001, 2], new Dictionary<uint, uint>(), new Dictionary<uint, uint>(), new Dictionary<uint, uint>()));
    }
}
