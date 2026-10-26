using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace MultiplayerEnvironmentEditor
{
    /// <summary>
    /// Resolves game types by name at runtime so the mod compiles regardless of
    /// which game assembly a type lives in on a given version.
    /// </summary>
    internal static class GameReflection
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<string, Type> Cache = new Dictionary<string, Type>();

        public static Type FindType(string name)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;

            Type found = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                found = types.FirstOrDefault(t => t.Name == name);
                if (found != null) break;
            }
            Cache[name] = found;
            return found;
        }

        public static Type FindTypeWhere(Func<Type, bool> predicate)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                var hit = types.FirstOrDefault(predicate);
                if (hit != null) return hit;
            }
            return null;
        }

        public static UnityEngine.Object[] AllEnvironments()
        {
            var t = FindType("EnvironmentInfoSO");
            return t == null ? new UnityEngine.Object[0] : Resources.FindObjectsOfTypeAll(t);
        }

        public static string SerializedName(object env) =>
            GetMember(env, "serializedName") as string;

        public static IEnumerable<string> EnvironmentNames() =>
            AllEnvironments()
                .Select(SerializedName)
                .Where(n => !string.IsNullOrEmpty(n) && !n.Contains("Multiplayer"))
                .Distinct()
                .OrderBy(n => n);

        public static UnityEngine.Object FindEnvironment(string serializedName) =>
            AllEnvironments().FirstOrDefault(e => SerializedName(e) == serializedName);

        /// <summary>The scene object inside an EnvironmentInfoSO (member name varies by version).</summary>
        public static UnityEngine.Object GetEnvironmentScene(object env)
        {
            foreach (var name in new[] { "sceneInfo", "_sceneInfo", "environmentScenes", "_environmentScenes" })
            {
                var val = GetMember(env, name);
                if (val is UnityEngine.Object single) return single;
                if (val is Array arr && arr.Length > 0 && arr.GetValue(0) is UnityEngine.Object first) return first;
            }
            return null;
        }

        public static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            return t.GetProperty(name, All)?.GetValue(obj) ?? t.GetField(name, All)?.GetValue(obj);
        }
    }
}
