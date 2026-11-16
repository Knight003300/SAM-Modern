using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SAM.Modern
{
    /// <summary>
    /// Steam appcache 二进制 KeyValue 读取器。
    /// 结构参考 SteamAchievementManager (zlib license, (c) 2024 Rick) 的 SAM.Game/KeyValue.cs。
    /// </summary>
    internal sealed class SchemaKv
    {
        private static readonly SchemaKv Invalid = new SchemaKv();

        public string Name = "<root>";
        public byte Type;
        public object Value;
        public bool Valid;
        public List<SchemaKv> Children = null;

        public SchemaKv this[string key]
        {
            get
            {
                if (this.Children == null) return Invalid;
                var child = this.Children.FirstOrDefault(
                    c => string.Compare(c.Name, key, StringComparison.InvariantCultureIgnoreCase) == 0);
                return child ?? Invalid;
            }
        }

        public string AsString(string defaultValue)
        {
            if (this.Valid == false || this.Value == null) return defaultValue;
            return this.Value.ToString();
        }

        public int AsInteger(int defaultValue)
        {
            if (this.Valid == false) return defaultValue;
            switch (this.Type)
            {
                case 1:
                case 5:
                {
                    int value;
                    return int.TryParse((string)this.Value, out value) == false ? defaultValue : value;
                }
                case 2: return (int)this.Value;
                case 3: return (int)((float)this.Value);
                case 7: return (int)(((ulong)this.Value) & 0xFFFFFFFF);
            }
            return defaultValue;
        }

        public float AsFloat(float defaultValue)
        {
            if (this.Valid == false) return defaultValue;
            switch (this.Type)
            {
                case 1:
                case 5:
                {
                    float value;
                    return float.TryParse((string)this.Value, out value) == false ? defaultValue : value;
                }
                case 2: return (int)this.Value;
                case 3: return (float)this.Value;
                case 7: return (ulong)this.Value & 0xFFFFFFFF;
            }
            return defaultValue;
        }

        public bool AsBoolean(bool defaultValue)
        {
            if (this.Valid == false) return defaultValue;
            switch (this.Type)
            {
                case 1:
                case 5:
                {
                    int value;
                    return int.TryParse((string)this.Value, out value) == false ? defaultValue : value != 0;
                }
                case 2: return ((int)this.Value) != 0;
                case 3: return ((int)((float)this.Value)) != 0;
                case 7: return ((ulong)this.Value) != 0;
            }
            return defaultValue;
        }

        public static SchemaKv LoadAsBinary(string path)
        {
            if (File.Exists(path) == false) return null;
            try
            {
                using (var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var kv = new SchemaKv();
                    return kv.ReadAsBinary(input) ? kv : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private bool ReadAsBinary(Stream input)
        {
            this.Children = new List<SchemaKv>();
            try
            {
                while (true)
                {
                    byte type = ReadU8(input);
                    if (type == 8) break;
                    var current = new SchemaKv { Type = type, Name = ReadStringUnicode(input) };
                    switch (type)
                    {
                        case 0:
                            current.ReadAsBinary(input);
                            break;
                        case 1:
                            current.Valid = true;
                            current.Value = ReadStringUnicode(input);
                            break;
                        case 5:
                            throw new FormatException("wstring is unsupported");
                        case 2:
                            current.Valid = true;
                            current.Value = ReadS32(input);
                            break;
                        case 7:
                            current.Valid = true;
                            current.Value = ReadU64(input);
                            break;
                        case 3:
                            current.Valid = true;
                            current.Value = ReadF32(input);
                            break;
                        case 6:
                        case 4:
                            current.Valid = true;
                            current.Value = ReadU32(input);
                            break;
                        default:
                            throw new FormatException();
                    }
                    if (input.Position >= input.Length) throw new FormatException();
                    this.Children.Add(current);
                }
                this.Valid = true;
                return input.Position == input.Length;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static byte ReadU8(Stream stream)
        {
            int b = stream.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            return (byte)b;
        }

        private static int ReadS32(Stream stream)
        {
            var data = new byte[4];
            if (stream.Read(data, 0, 4) != 4) throw new EndOfStreamException();
            return BitConverter.ToInt32(data, 0);
        }

        private static uint ReadU32(Stream stream)
        {
            var data = new byte[4];
            if (stream.Read(data, 0, 4) != 4) throw new EndOfStreamException();
            return BitConverter.ToUInt32(data, 0);
        }

        private static ulong ReadU64(Stream stream)
        {
            var data = new byte[8];
            if (stream.Read(data, 0, 8) != 8) throw new EndOfStreamException();
            return BitConverter.ToUInt64(data, 0);
        }

        private static float ReadF32(Stream stream)
        {
            var data = new byte[4];
            if (stream.Read(data, 0, 4) != 4) throw new EndOfStreamException();
            return BitConverter.ToSingle(data, 0);
        }

        private static string ReadStringUnicode(Stream stream)
        {
            const int chunk = 128;
            var sb = new StringBuilder();
            var buffer = new byte[4];
            while (true)
            {
                int read = stream.Read(buffer, 0, 1);
                if (read <= 0) break;
                if (buffer[0] == 0) break;
                var bytes = new List<byte>(chunk) { buffer[0] };
                bool finished = false;
                while (finished == false)
                {
                    int b = stream.ReadByte();
                    if (b < 0) { finished = true; break; }
                    if (b == 0) { finished = true; break; }
                    bytes.Add((byte)b);
                }
                sb.Append(Encoding.UTF8.GetString(bytes.ToArray()));
                if (finished) break;
            }
            return sb.ToString();
        }
    }
}
