// TEST FIXTURE ONLY. This deliberately small contract exercises package build
// integration before Chondrite.Store is published. It is not a store implementation
// and is never included in Chondrite.Compile's NuGet package.
using System.Buffers.Binary;
using System.Collections.Immutable;
using Chondrite.Core;

namespace Chondrite.Store
{
    public interface IObjectStore
    {
        StorageObjectId idPut(StorageObject oObject, StorageObjectId? idPredecessorHint = null);
        StorageObject? oGet(StorageObjectId idObject);
    }

    public static class StorageTypeRegistration
    {
        public static StorageType idRegisterType(this IObjectStore oStore, ImmutableArray<byte> abySchema)
        {
            StorageObject oSchema = new(StorageType.idIntrinsicSchema, abySchema);
            return new StorageType(oStore.idPut(oSchema));
        }
    }
}

namespace Chondrite.Store.Serialization
{
    // Test-only stand-in for the runtime decode limits. It intentionally keeps
    // the same public shape as Chondrite.Store without adding a package dependency.
    public readonly record struct StorageDecodeLimits(
        int nMaxCollectionElements,
        long nMaxMaterializedBytes)
    {
        public static StorageDecodeLimits oDefault { get; } =
            new(1_000_000, int.MaxValue);
    }

    public interface IStorageRecord<TSelf> where TSelf : IStorageRecord<TSelf>
    {
        static abstract StorageType idSchema { get; }
        static abstract void EnsureSchema(IObjectStore oStore);
        static abstract StorageObject oSerialize(in TSelf oValue);
        static abstract TSelf oDeserialize(StorageObject oObject);
    }

    public sealed class StorageBinaryWriter
    {
        private readonly MemoryStream m_oStream = new();

        public void WriteFloat64(double fValue)
        {
            Span<byte> abyValue = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(abyValue, fValue);
            m_oStream.Write(abyValue);
        }

        public void WriteUInt32(uint nValue)
        {
            Span<byte> abyValue = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(abyValue, nValue);
            m_oStream.Write(abyValue);
        }

        public byte[] aToArray() => m_oStream.ToArray();
    }

    public ref struct StorageBinaryReader
    {
        private ReadOnlySpan<byte> m_abyRemaining;
        private int m_nRemainingCollectionElements;

        public StorageBinaryReader(ReadOnlySpan<byte> abyData, StorageDecodeLimits? oLimits = null)
        {
            StorageDecodeLimits oEffective = oLimits ?? StorageDecodeLimits.oDefault;
            if (oEffective.nMaxCollectionElements < 0 || oEffective.nMaxMaterializedBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(oLimits));
            m_abyRemaining = abyData;
            m_nRemainingCollectionElements = oEffective.nMaxCollectionElements;
        }

        public void GuardCollectionCount(int nCount, int nMinimumElementBytes)
        {
            if (nCount < 0 || nMinimumElementBytes < 0)
                throw new ArgumentOutOfRangeException();
            if (nCount > m_nRemainingCollectionElements ||
                (nMinimumElementBytes > 0 && nCount > m_abyRemaining.Length / nMinimumElementBytes))
                throw new InvalidDataException("Invalid collection size in package smoke fixture.");
            m_nRemainingCollectionElements -= nCount;
        }

        public double ReadFloat64()
        {
            double fValue = BinaryPrimitives.ReadDoubleLittleEndian(m_abyRemaining);
            m_abyRemaining = m_abyRemaining[8..];
            return fValue;
        }

        public uint ReadUInt32()
        {
            uint nValue = BinaryPrimitives.ReadUInt32LittleEndian(m_abyRemaining);
            m_abyRemaining = m_abyRemaining[4..];
            return nValue;
        }

        public void EnsureEnd()
        {
            if (!m_abyRemaining.IsEmpty)
                throw new InvalidDataException("Trailing data in package smoke fixture.");
        }
    }
}
