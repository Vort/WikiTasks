﻿using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace WikiTasks
{
    class Api
    {
        public static string UrlEncode(string s)
        {
            const string unreserved = "abcdefghijklmnopqrstuvwxyz" +
                "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.~";
            var sb = new StringBuilder();
            var bytes = Encoding.UTF8.GetBytes(s);
            foreach (byte b in bytes)
            {
                if (unreserved.Contains((char)b))
                    sb.Append((char)b);
                else
                    sb.Append($"%{b:X2}");
            }
            return sb.ToString();
        }

        public static string GZipUnpack(byte[] data)
        {
            using (var msi = new MemoryStream(data))
            {
                using (var gzs = new GZipStream(msi, CompressionMode.Decompress))
                {
                    using (var mso = new MemoryStream())
                    {
                        gzs.CopyTo(mso);
                        return Encoding.UTF8.GetString(mso.ToArray());
                    }
                }
            }
        }
    }
}
