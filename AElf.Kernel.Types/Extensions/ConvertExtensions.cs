﻿using System;
using System.Linq;
using System.Runtime.CompilerServices;

namespace AElf.Kernel
{
    public static class ConvertExtensions
    {
        public static byte[] ToBytes(this ulong number)
        {
            return BitConverter.IsLittleEndian ? 
                BitConverter.GetBytes(number).Reverse().ToArray() : 
                BitConverter.GetBytes(number);
        }

        public static ulong ToUInt64(this byte[] bytes)
        {
            return BitConverter.ToUInt64(
                BitConverter.IsLittleEndian ? 
                    bytes.Reverse().ToArray() : 
                    bytes, 0);
        }

        public static string RemoveHexPrefix(this string hexStr)
        {
            return hexStr.StartsWith("0x") ? hexStr.Remove(0, 2) : hexStr;
        }

        public static string AppendHexPrefix(this string str)
        {
            return str.StartsWith("0x") ? str : "0x" + str;
        }
    }
}