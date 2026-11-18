using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SAM.API;

namespace SAM.Modern
{
    internal static class SchemaStatKind
    {
        public const int Integer = 1;
        public const int Float = 2;
        public const int AverageRate = 3;
        public const int Achievements = 4;
        public const int GroupAchievements = 5;
    }

    internal sealed class AchievementDef
    {
        public string Id;
        public string Name;
        public string Description;
        public string IconNormal;
        public string IconLocked;
        public bool IsHidden;
        public int Permission;
    }

    internal sealed class StatDefinition
    {
        public string Id;
        public string Name;
        public int Kind;
        public int MinValue = int.MinValue;
        public int MaxValue = int.MaxValue;
        public int MaxChange;
        public bool IncrementOnly;
        public bool SetByTrustedGameServer;
        public int DefaultValue;
        public int Permission;

        public bool IsFloat { get { return this.Kind == SchemaStatKind.Float || this.Kind == SchemaStatKind.AverageRate; } }
        public bool IsProtected { get { return (this.Permission & 2) != 0; } }
    }

    internal static class SchemaLoader
    {
        public static string SchemaPath(long appId)
        {
            string install = Steam.GetInstallPath();
            if (string.IsNullOrEmpty(install)) return null;
            return Path.Combine(install, "appcache", "stats", "UserGameStatsSchema_" + appId.ToString(CultureInfo.InvariantCulture) + ".bin");
        }

        public static string GetLocalizedString(SchemaKv root, string language, string fallback)
        {
            if (root == null || root.Valid == false) return fallback;

            if (string.IsNullOrEmpty(language) == false)
            {
                var localized = root[language];
                if (localized.Valid && string.IsNullOrEmpty(localized.AsString(null)) == false) return localized.AsString(fallback);
            }

            if (string.Equals(language, "english", StringComparison.OrdinalIgnoreCase) == false)
            {
                var english = root["english"];
                if (english.Valid && string.IsNullOrEmpty(english.AsString(null)) == false) return english.AsString(fallback);
            }

            if (string.IsNullOrEmpty(root.AsString(null)) == false) return root.AsString(fallback);
            return fallback;
        }

        /// <summary>后台预读结果：解析失败的容器为 null，调用方回落到同步 Load。</summary>
        internal sealed class Preloaded
        {
            public bool Ok;
            public List<AchievementDef> Achievements;
            public List<StatDefinition> Stats;
        }

        /// <summary>在后台线程解析架构文件，返回可直接使用的结果（线程安全：只读本地文件）。</summary>
        public static Preloaded Preload(long appId, string language)
        {
            var result = new Preloaded();
            try
            {
                List<AchievementDef> achievements;
                List<StatDefinition> stats;
                if (Load(appId, language, out achievements, out stats))
                {
                    result.Ok = true;
                    result.Achievements = achievements;
                    result.Stats = stats;
                }
            }
            catch (Exception error)
            {
                Log.Write("schema preload failed", error);
            }
            return result;
        }

        public static bool Load(long appId, string language,
            out List<AchievementDef> achievements, out List<StatDefinition> stats)
        {
            achievements = new List<AchievementDef>();
            stats = new List<StatDefinition>();

            string path = SchemaPath(appId);
            if (path == null || File.Exists(path) == false)
            {
                Log.Write("schema missing: " + path);
                return false;
            }

            var kv = SchemaKv.LoadAsBinary(path);
            if (kv == null)
            {
                Log.Write("schema parse failed: " + path);
                return false;
            }

            var game = kv[appId.ToString(CultureInfo.InvariantCulture)];
            if (game.Valid == false)
            {
                Log.Write("schema has no app node: " + path);
                return false;
            }

            var statsRoot = game["stats"];
            if (statsRoot.Children == null || statsRoot.Children.Count == 0)
            {
                Log.Write("schema has no stats: " + path);
                return false;
            }

            foreach (var stat in statsRoot.Children)
            {
                int kind = ParseKind(stat);

                if (kind == SchemaStatKind.Integer || kind == SchemaStatKind.Float || kind == SchemaStatKind.AverageRate)
                {
                    var id = stat["name"].AsString(null);
                    if (string.IsNullOrEmpty(id)) continue;

                    var def = new StatDefinition
                    {
                        Id = id,
                        Kind = kind,
                        Name = GetLocalizedString(stat["display"]["name"], language, id),
                        MinValue = stat["min"].AsInteger(int.MinValue),
                        MaxValue = stat["max"].AsInteger(int.MaxValue),
                        MaxChange = stat["maxchange"].AsInteger(0),
                        IncrementOnly = stat["incrementonly"].AsBoolean(false),
                        SetByTrustedGameServer = stat["bSetByTrustedGS"].AsBoolean(false),
                        DefaultValue = stat["default"].AsInteger(0),
                        Permission = stat["permission"].AsInteger(0),
                    };
                    stats.Add(def);
                }
                else if (kind == SchemaStatKind.Achievements || kind == SchemaStatKind.GroupAchievements)
                {
                    var bits = stat["bits"];
                    if (bits.Children == null) continue;
                    foreach (var bit in bits.Children)
                    {
                        var id = bit["name"].AsString(null);
                        if (string.IsNullOrEmpty(id)) continue;

                        var def = new AchievementDef
                        {
                            Id = id,
                            Name = GetLocalizedString(bit["display"]["name"], language, id),
                            Description = GetLocalizedString(bit["display"]["desc"], language, string.Empty),
                            IconNormal = bit["display"]["icon"].AsString(null),
                            IconLocked = bit["display"]["icon_gray"].AsString(null),
                            IsHidden = bit["display"]["hidden"].AsBoolean(false),
                            Permission = bit["permission"].AsInteger(0),
                        };
                        if (string.IsNullOrEmpty(def.IconLocked)) def.IconLocked = def.IconNormal;
                        achievements.Add(def);
                    }
                }
            }

            Log.Write("schema loaded: " + achievements.Count + " achievements, " + stats.Count + " stats");
            return true;
        }

        private static int ParseKind(SchemaKv stat)
        {
            var type = stat["type"];
            if (type.Valid)
            {
                string name = type.AsString(null);
                if (string.IsNullOrEmpty(name) == false)
                {
                    // 新版 schema 存的是 Steam 的 UserStatType 枚举名（INT / FLOAT / AVGRATE / ...），
                    // 旧版存的是小写全名（integer / float / averagerate / ...），两种都要认。
                    switch (name.Trim().ToLowerInvariant())
                    {
                        case "int":
                        case "integer": return SchemaStatKind.Integer;
                        case "float": return SchemaStatKind.Float;
                        case "avgrate":
                        case "averagerate": return SchemaStatKind.AverageRate;
                        case "achievements": return SchemaStatKind.Achievements;
                        case "groupachievements": return SchemaStatKind.GroupAchievements;
                    }
                }
            }
            return stat["type_int"].AsInteger(0);
        }
    }
}
