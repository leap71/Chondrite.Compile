using System.Collections.Immutable;
using System.Numerics;
using Chondrite.Core;
using Chondrite.Store;
using Chondrite.Store.Serialization;
using Smoke;

HashSet<Type> aTested = [];

Check<BoolProbe, bool>(true, 1, v => new([v]), o => o.values);
Check<Int8Probe, sbyte>(-5, 1, v => new([v]), o => o.values);
Check<UInt8Probe, byte>(201, 1, v => new([v]), o => o.values);
Check<Int16Probe, short>(-1234, 2, v => new([v]), o => o.values);
Check<UInt16Probe, ushort>(54321, 2, v => new([v]), o => o.values);
Check<Int32Probe, int>(-123456, 4, v => new([v]), o => o.values);
Check<UInt32Probe, uint>(123456U, 4, v => new([v]), o => o.values);
Check<Int64Probe, long>(-12345678901L, 8, v => new([v]), o => o.values);
Check<UInt64Probe, ulong>(12345678901UL, 8, v => new([v]), o => o.values);
Check<Float32Probe, float>(1.25f, 4, v => new([v]), o => o.values);
Check<Float64Probe, double>(-1.25, 8, v => new([v]), o => o.values);
Check<Vector2fProbe, Vector2>(new(1, -2), 8, v => new([v]), o => o.values);
Check<Vector3fProbe, Vector3>(new(1, -2, 3), 12, v => new([v]), o => o.values);
Check<Vector4fProbe, Vector4>(new(1, -2, 3, -4), 16, v => new([v]), o => o.values);
Check<GuidProbe, Guid>(new("00112233-4455-6677-8899-aabbccddeeff"), 16, v => new([v]), o => o.values);
StorageBinaryReader oNonceReader = new(Convert.FromHexString("00112233445566778899aabbccddeeff"),
    StorageDecodeLimits.oDefault);
Check<NonceProbe, Nonce>(oNonceReader.ReadNonce(), 16, v => new([v]), o => o.values);
StorageObjectId idSample = new(Convert.FromHexString(
    "00112233445566778899aabbccddeeffffeeddccbbaa99887766554433221100"));
Check<ObjectIdProbe, StorageObjectId>(idSample, 32, v => new([v]), o => o.values);

// Empty contents attain the minimum width; also check a multibyte string and a binary value.
Check<Utf8Probe, string>(string.Empty, 8, v => new([v]), o => o.values);
Check<Utf8Probe, string>("Aé", 11, v => new([v]), o => o.values);
Check<BytesProbe, ImmutableArray<byte>>([], 8, v => new([v]), o => o.values, BytesEqual);
Check<BytesProbe, ImmutableArray<byte>>([0, 128, 255], 11, v => new([v]), o => o.values, BytesEqual);

Type[] aMissing = typeof(Program).Assembly.GetExportedTypes()
    .Where(oType => oType.Namespace == "Smoke" && !aTested.Contains(oType))
    .ToArray();
if (aMissing.Length != 0)
    throw new InvalidOperationException($"Missing primitive probes: {string.Join(", ", aMissing.Select(oType => oType.Name))}.");

Console.WriteLine($"Verified payload widths and readback for {aTested.Count} primitives through the published Store.");

static bool BytesEqual(ImmutableArray<byte> a, ImmutableArray<byte> b) =>
    !a.IsDefault && !b.IsDefault && a.AsSpan().SequenceEqual(b.AsSpan());

void Check<TRecord, TValue>(TValue oExpected, int nExpectedBytes,
    Func<TValue, TRecord> xCreate, Func<TRecord, ImmutableArray<TValue>> xValues,
    Func<TValue, TValue, bool>? xEqual = null) where TRecord : IStorageRecord<TRecord>
{
    TRecord oRecord = xCreate(oExpected);
    StorageObject oObject = TRecord.oSerialize(in oRecord);
    if (oObject.abyData.Length != nExpectedBytes)
        throw new InvalidOperationException(
            $"{typeof(TRecord).Name}: expected {nExpectedBytes} bytes; encoded {oObject.abyData.Length}.");

    ImmutableArray<TValue> aActual = xValues(TRecord.oDeserialize(oObject));
    xEqual ??= EqualityComparer<TValue>.Default.Equals;
    if (aActual.IsDefault || aActual.Length != 1 || !xEqual(oExpected, aActual[0]))
        throw new InvalidOperationException($"{typeof(TRecord).Name}: generated readback differs from the input.");

    aTested.Add(typeof(TRecord));
}
