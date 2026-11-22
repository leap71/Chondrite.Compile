// Test-only probe: use constructors and accessors scaffolded by the packed compiler.
using System.Reflection;
using Chondrite.Core;
using Smoke;

Sample oExpected = new(101325.25);
StorageObject oObject = Sample.oSerialize(in oExpected);
Sample oActual = Sample.oDeserialize(oObject);
if (oActual.fValue != oExpected.fValue || oActual.fValue == 0)
    throw new InvalidOperationException("The scaffolded public interface did not round-trip a non-default value.");

if (args.Contains("custom", StringComparer.Ordinal))
{
    PropertyInfo oLabel = typeof(Sample).GetProperty("strLocalLabel")
        ?? throw new InvalidOperationException("The moved, edited partial was not compiled.");
    object oBoxed = oExpected;
    oLabel.SetValue(oBoxed, "local-only value");
    Sample oCustomized = (Sample)oBoxed;
    StorageObject oCustomizedObject = Sample.oSerialize(in oCustomized);
    if ((string?)oLabel.GetValue(oCustomized) != "local-only value" ||
        oCustomizedObject.idCalculateIdentity() != oObject.idCalculateIdentity() ||
        oLabel.GetValue(Sample.oDeserialize(oCustomizedObject)) is not null)
        throw new InvalidOperationException("User instance state affected the persisted representation.");
}

Console.WriteLine("Verified the scaffolded constructor, accessor, and canonical round-trip.");
