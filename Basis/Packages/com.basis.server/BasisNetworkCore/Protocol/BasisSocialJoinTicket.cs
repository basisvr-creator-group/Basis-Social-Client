using System;
using System.Text;
using static Basis.Network.Core.Serializable.SerializableBasis;

namespace Basis.Network.Core
{
    /// <summary>Optional handshake extension. A ticket grants nothing until the existing DID challenge succeeds.</summary>
    public static class BasisSocialJoinTicket
    {
        public static bool IsValid(string value)
        {
            if (value == null || value.Length != 50 || !value.StartsWith("bvr_jt_", StringComparison.Ordinal)) return false;
            for (int i = 7; i < value.Length; i++)
            {
                char c = value[i];
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_' && c != '-') return false;
            }
            return true;
        }

        public static void Write(NetDataWriter writer, string ticket)
        {
            if (string.IsNullOrEmpty(ticket)) return;
            if (!IsValid(ticket)) throw new ArgumentException("Invalid instance ticket.", nameof(ticket));
            new BytesMessage().Serialize(writer, Encoding.ASCII.GetBytes(ticket));
        }

        public static bool TryRead(NetDataReader reader, out string ticket)
        {
            ticket = null;
            if (reader.AvailableBytes == 0) return true;
            if (reader.AvailableBytes != 52 || !new BytesMessage().Deserialize(reader, out byte[] bytes) || !reader.EndOfData) return false;
            ticket = Encoding.ASCII.GetString(bytes);
            return IsValid(ticket);
        }
    }
}
