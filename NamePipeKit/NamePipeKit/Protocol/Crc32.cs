using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NamePipeKit.NamePipeKit.Protocol
{
    /// <summary>标准 CRC32（多项式 0xEDB88320，查表法）。查表法，一次 8 位，比逐位计算快 8 倍。
    /// <para>用于 PipeFrame 的 Header 校验。</para>
    /// </summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        /// <summary>计算指定区间的 CRC32。</summary>
        public static uint Compute(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < length; i++)
                crc = Table[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }
    }
}
