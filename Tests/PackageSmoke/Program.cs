// Test-only probe: check the types actually compiled into the consuming assembly.
using System.Reflection;
using Chondrite.Core;

Assembly oAssembly = typeof(Program).Assembly;
string[] astrExpected = args.Order(StringComparer.Ordinal).ToArray();
string[] astrActual = oAssembly.GetTypes()
    .Where(oType => oType.IsPublic && oType.Namespace == "Smoke")
    .Select(oType => oType.FullName!)
    .Order(StringComparer.Ordinal)
    .ToArray();

if (!astrActual.SequenceEqual(astrExpected))
    throw new InvalidOperationException(
        $"Generated types differ. Expected: {string.Join(", ", astrExpected)}; actual: {string.Join(", ", astrActual)}.");

foreach (string strName in astrExpected)
{
    Type oType = oAssembly.GetType(strName, throwOnError: true)!;
    object oValue = Activator.CreateInstance(oType)!;
    StorageType idSchema = (StorageType)oType.GetProperty("idSchema")!.GetValue(null)!;
    StorageObject oObject = (StorageObject)oType.GetMethod("oSerialize")!.Invoke(null, [oValue])!;
    if (oObject.idType != idSchema)
        throw new InvalidOperationException("Generated object has an unexpected schema identity.");

    object oRoundTrip = oType.GetMethod("oDeserialize")!.Invoke(null, [oObject])!;
    if (!oValue.Equals(oRoundTrip))
        throw new InvalidOperationException("Generated record did not round-trip through the test fixture.");
}

Console.WriteLine($"Verified {astrActual.Length} generated type(s) in the consuming assembly.");
