using System;
using System.Collections.Generic;
using System.Reflection;

namespace XTool
{
    /// <summary>
    /// 自動從組件中找出所有 IExternalToolAction 實作，
    /// 不需要手動 Register(new XxxAction())。
    /// </summary>
    internal static class ActionRegistry
    {
        private static readonly Dictionary<string, IExternalToolAction> _actions =
            new Dictionary<string, IExternalToolAction>(StringComparer.OrdinalIgnoreCase);

        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            Type iface = typeof(IExternalToolAction);
            Assembly asm = iface.Assembly; // 通常就是 XTool.exe 本身

            Type[] types = asm.GetTypes();
            int i;
            for (i = 0; i < types.Length; i++)
            {
                Type t = types[i];

                // 跳過抽象類 / 介面
                if (t.IsAbstract || t.IsInterface)
                {
                    continue;
                }

                // 只收 IExternalToolAction
                if (!iface.IsAssignableFrom(t))
                {
                    continue;
                }

                // 若你只想收 XTool.Module 底下的：
                if (t.Namespace == null || !t.Namespace.StartsWith("XTool.Module"))
                {
                    continue;
                }

                IExternalToolAction instance;
                try
                {
                    instance = (IExternalToolAction)Activator.CreateInstance(t);
                }
                catch
                {
                    // 建構失敗直接略過（避免整個系統掛掉）
                    continue;
                }

                string name = instance.Name;
                if (string.IsNullOrEmpty(name))
                {
                    // 沒給 Name 就略過，避免註冊成空字串
                    continue;
                }

                if (!_actions.ContainsKey(name))
                {
                    _actions.Add(name, instance);
                }
                // 若重複 Name，可以視需要決定是否覆蓋，這裡先以第一個為主
            }
        }

        /// <summary>
        /// 根據 action 名稱取得對應的 IExternalToolAction 實例。
        /// 先從已掃描的表中找，如果找不到再試著用命名慣例
        /// XTool.Module.{name}Action 來找 Type。
        /// </summary>
        public static IExternalToolAction GetAction(string actionName)
        {
            if (string.IsNullOrEmpty(actionName))
            {
                return null;
            }

            if (!_initialized)
            {
                Initialize();
            }

            IExternalToolAction act;
            if (_actions.TryGetValue(actionName, out act))
            {
                return act;
            }

            // ---- Fallback：命名慣例 XTool.Module.{ActionName}Action ----
            string typeName = "XTool.Module." + actionName + "Action";

            Type t = Type.GetType(typeName);
            if (t != null &&
                !t.IsAbstract &&
                typeof(IExternalToolAction).IsAssignableFrom(t))
            {
                try
                {
                    act = (IExternalToolAction)Activator.CreateInstance(t);
                    _actions[actionName] = act; // cache 起來
                    return act;
                }
                catch
                {
                    // 建構失敗就當作沒有
                }
            }

            return null;
        }

        /// <summary>
        /// 有時候你想列出目前支援哪些 action，可以用這個。
        /// </summary>
        public static IEnumerable<string> GetRegisteredActionNames()
        {
            if (!_initialized)
            {
                Initialize();
            }

            return _actions.Keys;
        }
    }
}
