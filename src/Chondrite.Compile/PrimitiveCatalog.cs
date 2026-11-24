using System.Collections.Frozen;

namespace Chondrite.Compile;

internal sealed record PrimitiveDescriptor(string Token, string CSharpType, string WriterMethod,
    string ReaderMethod, int MinimumEncodedBytes, bool WriteByteSpan = false);

internal static class PrimitiveCatalog
{
    // V1 Store encoding. Even empty strings and byte arrays have an eight-byte length prefix.
    private static readonly FrozenDictionary<string, PrimitiveDescriptor> c_oByToken =
        new PrimitiveDescriptor[]
        {
            new("bool", "bool", "WriteBool", "ReadBool", 1),
            new("int8", "sbyte", "WriteInt8", "ReadInt8", 1),
            new("uint8", "byte", "WriteUInt8", "ReadUInt8", 1),
            new("int16", "short", "WriteInt16", "ReadInt16", 2),
            new("uint16", "ushort", "WriteUInt16", "ReadUInt16", 2),
            new("int32", "int", "WriteInt32", "ReadInt32", 4),
            new("uint32", "uint", "WriteUInt32", "ReadUInt32", 4),
            new("int64", "long", "WriteInt64", "ReadInt64", 8),
            new("uint64", "ulong", "WriteUInt64", "ReadUInt64", 8),
            new("float32", "float", "WriteFloat32", "ReadFloat32", 4),
            new("float64", "double", "WriteFloat64", "ReadFloat64", 8),
            new("utf8", "string", "WriteUtf8", "ReadUtf8", 8),
            new("bytes", "System.Collections.Immutable.ImmutableArray<byte>", "WriteBytes", "ReadBytes", 8,
                WriteByteSpan: true),
            new("vector2f", "global::System.Numerics.Vector2", "WriteVector2f", "ReadVector2f", 8),
            new("vector3f", "global::System.Numerics.Vector3", "WriteVector3f", "ReadVector3f", 12),
            new("vector4f", "global::System.Numerics.Vector4", "WriteVector4f", "ReadVector4f", 16),
            new("guid", "System.Guid", "WriteGuid", "ReadGuid", 16),
            new("nonce", "global::Chondrite.Store.Nonce", "WriteNonce", "ReadNonce", 16),
            new("objectid", "global::Chondrite.Core.StorageObjectId", "WriteObjectId", "ReadObjectId", 32)
        }.ToFrozenDictionary(x => x.Token, StringComparer.Ordinal);

    internal static IEnumerable<string> Tokens => c_oByToken.Keys;

    internal static bool Contains(string strToken) => c_oByToken.ContainsKey(strToken);

    internal static PrimitiveDescriptor Get(string strToken) =>
        c_oByToken.TryGetValue(strToken, out PrimitiveDescriptor? oDescriptor)
            ? oDescriptor
            : throw new NotSupportedException($"Unknown primitive '{strToken}'.");
}
