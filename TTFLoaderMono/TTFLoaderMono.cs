// 外部别名引用：允许引用来自不同程序集的相同命名空间下的类型，解决冲突。
// 这里的 'textrender' 别名很可能用于区分特定的 Unity.Font 类型。
extern alias textrender;

// 显式使用 textrender 外部别名中的 UnityEngine.Font 类型，将其命名为 Font。
// 这确保了代码中使用的是这个特定程序集（可能是某个插件或目标游戏依赖）的 Font 类型。
using Font = textrender::UnityEngine.Font;


using BepInEx;

// if BEPINEX_V6
// using BepInEx.Unity.Mono;


using System;
using System.IO;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine.SceneManagement;

namespace TTFLoaderMono
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class TTFLoaderPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.github.you9you.ttfloader";
        public const string PLUGIN_NAME = "TTF Font Loader (Mono)";
        public const string PLUGIN_VERSION = "1.1.0";

        private static string fontsDirectory;

        // 用于替换 UnityEngine.UI.Text 组件的字体
        private static Font dynamicFont;

        // 用于替换场景中已存在的 TextMeshProUGUI 组件的字体
        // （仅修改 TMP_Settings.defaultFontAsset 不会刷新已经在场景里实例化的 TMP 文字）
        private static TMP_FontAsset customTmpFont;

        /// <summary>
        /// BepInEx 插件初始化时调用，类似于 Unity 的 Awake
        /// </summary>
        void Awake()
        {
            Logger.LogInfo($"Plugin {PLUGIN_NAME} is loaded!");

            // 初始化字体目录路径
            // fontsDirectory = Path.Combine(BepInEx.Paths.PluginPath, "Fonts");
            // if (!Directory.Exists(fontsDirectory))
            // {
            //     Directory.CreateDirectory(fontsDirectory);
            //     Logger.LogWarning($"Fonts directory created: {fontsDirectory}");
            // }

            // 使用游戏根目录作为字体加载路径
            fontsDirectory = BepInEx.Paths.GameRootPath;

            Logger.LogInfo($"TTF Loader initialized. Fonts directory: {fontsDirectory}");
        }

        /// <summary>
        /// 插件启动时调用，类似于 Unity 的 Start
        /// </summary>
        void Start()
        {
            // 从预设的目录中查找并加载第一个可用的字体
            // （该方法内部在成功后会主动遍历一次场景里现存的 TextMeshProUGUI 组件）
            LoadDefaultFontFromDirectory();

            // 启动延迟协程，覆盖后续 Start/Awake 才实例化的 TMP 组件
            // （典型于 Naninovel 这类动态 UI 系统）
            StartCoroutine(ApplyFontAfterDelay());

            // 启动持续轮询协程，覆盖场景切换后通过对话/动画等事件
            // 延迟实例化的 TMP 组件（例如 Naninovel 的对话框、说话人名字框、
            // 对话日志等）。
            StartCoroutine(PollAndApplyFontForever());

            // 订阅场景加载完成事件，以便在新场景加载后应用字体
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// <summary>
        /// 插件销毁时调用，用于资源清理
        /// </summary>
        void OnDestroy()
        {
            // 取消订阅场景加载事件，避免内存泄漏或在插件卸载后调用不存在的方法
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        /// <summary>
        /// 场景加载完成事件的处理函数
        /// </summary>
        /// <param name="scene">已加载的场景</param>
        /// <param name="mode">场景加载模式</param>
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // 启动协程，延迟应用字体，确保新场景的 UI 组件已完全初始化
            StartCoroutine(ApplyFontAfterDelay());

            // 同时启动持续轮询协程，覆盖场景切换后通过对话/动画等事件
            // 延迟实例化的 TMP 组件（例如 Naninovel 的对话框、说话人名字框、
            // 对话日志等）。StartCoroutine 保证同名字协程可以并行存在多份。
            StartCoroutine(PollAndApplyFontForever());
        }

        /// <summary>
        /// 延迟应用字体的协程
        /// </summary>
        private IEnumerator ApplyFontAfterDelay()
        {
            yield return null; // 等待当前帧结束（等待 Unity UI 初始化）
            yield return null; // 再等一帧（更保险，确保所有 Start/Awake 完成）

            // 延迟后，对场景中的所有 UI.Text / TextMeshProUGUI 应用自定义字体
            ApplyCustomFontToAllTexts();
            ApplyCustomFontToAllTMPTexts();
        }

        /// <summary>
        /// 持续轮询协程：每隔 0.25 秒检查一次场景中所有 TextMeshProUGUI 与 UI.Text，
        /// 替换掉仍然引用旧字体的组件。
        ///
        /// 用途：Naninovel 这类引擎会在玩家触发对话/翻页等事件后才把对话框、
        /// 说话人名字框、对话日志的 Prefab Instantiate 到场景里，单纯靠
        /// SceneManager.sceneLoaded 钩子覆盖不到。本协程以低频率轮询，
        /// 性能开销可忽略（一帧内只是对几十个组件做引用相等性判断）。
        /// </summary>
        private IEnumerator PollAndApplyFontForever()
        {
            // 首次立即执行一次（与 ApplyFontAfterDelay 并行，但查找的快照可能不同，
            // 因为多了 1~2 帧让更多 Start/Awake 完成）
            ApplyCustomFontToAllTMPTexts();
            ApplyCustomFontToAllTexts();

            var wait = new UnityEngine.WaitForSecondsRealtime(0.25f);
            while (true)
            {
                yield return wait;
                ApplyCustomFontToAllTMPTexts();
                ApplyCustomFontToAllTexts();
            }
        }

        /// <summary>
        /// 查找场景中所有的 UnityEngine.UI.Text 组件并应用加载的动态字体
        /// </summary>
        private void ApplyCustomFontToAllTexts()
        {
            if (dynamicFont == null)
            {
                return;
            }

            int replaced = 0;
            int scanned = 0;
            foreach (var text in FindAllTextComponents(includeInactive: true))
            {
                if (text == null)
                {
                    continue;
                }
                scanned++;
                if (text.font == dynamicFont)
                {
                    continue;
                }
                // 详细诊断：输出场景层级路径与原字体名，方便定位哪些 UI.Text 没替换
                string oldFontName = text.font != null ? text.font.name : "<null>";
                Logger.LogInfo(
                    $"[UI.Text] Replacing font on '{GetScenePath(text)}' " +
                    $"(active={text.gameObject.activeInHierarchy}, original='{oldFontName}')");
                text.font = dynamicFont;
                replaced++;
            }
            if (replaced > 0)
            {
                Logger.LogInfo($"Applied custom UnityEngine.Font to {replaced}/{scanned} UI.Text component(s).");
            }
        }

        /// <summary>
        /// 查找场景中所有的 TextMeshProUGUI 组件并替换为自定义 TMP_FontAsset。
        /// 仅修改 TMP_Settings.defaultFontAsset 不会影响已经实例化的 TMP 文字（它们各自的 .font 仍指向旧字体），
        /// 因此必须主动遍历并替换。该方法对反射查询到的所有组件（含非激活）一视同仁。
        ///
        /// 除了替换 .font 引用，还会在每次轮询中检查组件当前文本是否含尚未收录进
        /// customTmpFont 图集的字符——若有则调用 TryAddCharacters 补齐，避免 Naninovel
        /// 动态注入中文/韩文等非 ASCII 字符时显示豆腐块（□）。
        /// </summary>
        private void ApplyCustomFontToAllTMPTexts()
        {
            if (customTmpFont == null)
            {
                return;
            }

            int replaced = 0;
            int scanned = 0;
            int expanded = 0;
            int meshRebuilt = 0;
            foreach (var tmp in FindAllTMPComponents(includeInactive: true))
            {
                if (tmp == null)
                {
                    continue;
                }
                scanned++;
                string text = tmp.text;

                // 步骤 1：替换 .font 引用。如果当前引用的是我们设置的字体，跳过替换但仍继续走字符补齐 + 重建 mesh 的路径，
                // 因为 Naninovel 可能在我们看不见的地方切换过 fontAsset 引用。
                if (tmp.font != customTmpFont)
                {
                    string oldFontName = tmp.font != null ? tmp.font.name : "<null>";
                    string preview = text ?? string.Empty;
                    if (preview.Length > 32)
                    {
                        preview = preview.Substring(0, 32) + "…";
                    }
                    Logger.LogInfo(
                        $"[TextMeshProUGUI] Replacing font on '{GetScenePath(tmp)}' " +
                        $"(active={tmp.gameObject.activeInHierarchy}, original='{oldFontName}', text='{preview}')");
                    tmp.font = customTmpFont;
                    replaced++;
                }

                // 步骤 2：检查当前文本是否含未收录字符，若有则补齐图集。
                // 对 Dynamic 模式这只是预防——即便图集缺字符，TMP 在 ForceMeshUpdate 时也会自动从 sourceFontFile 补。
                if (!string.IsNullOrEmpty(text))
                {
                    List<char> missingChars;
                    if (!customTmpFont.HasCharacters(text, out missingChars) &&
                        missingChars != null && missingChars.Count > 0)
                    {
                        var sbMissing = new System.Text.StringBuilder(missingChars.Count);
                        foreach (var c in missingChars)
                        {
                            sbMissing.Append(c);
                        }
                        bool added = customTmpFont.TryAddCharacters(sbMissing.ToString());
                        if (added)
                        {
                            expanded++;
                            if (expanded <= 3)
                            {
                                Logger.LogInfo(
                                    $"[TextMeshProUGUI] Expanded customTmpFont atlas by " +
                                    $"{missingChars.Count} glyph(s) for '{GetScenePath(tmp)}'");
                            }
                        }
                    }
                }

                // 步骤 3：强制 mesh 重建。设置 .font 之后 TMP 并不会自动重新排版，
                // 必须显式调用 ForceMeshUpdate 才能让当前 text 用新字体重新生成顶点/UV。
                // 这对 Naninovel 这种在 PlayerLoop 里直接修改 .text 的场景至关重要。
                if (tmp.gameObject.activeInHierarchy)
                {
                    tmp.ForceMeshUpdate(true, true);
                    meshRebuilt++;
                }
            }
            if (replaced > 0 || expanded > 0 || meshRebuilt > 0)
            {
                Logger.LogInfo(
                    $"Applied custom TMP font to {replaced}/{scanned} TextMeshProUGUI component(s), " +
                    $"atlas expanded for {expanded}, mesh rebuilt for {meshRebuilt}.");
            }
        }

        /// <summary>
        /// 返回组件在场景中的完整层级路径，例如
        ///   "Naninovel<Runtime>/UI/InvenUI/Pages/PrevPageButton/Text"
        /// 当组件不在任何加载场景中时回退到 GameObject 名。
        /// </summary>
        private static string GetScenePath(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return "<null>";
            }

            // 优先走 Transform.parent 链（GameObject 与 Component 都可拿到 transform）
            var transform = obj is UnityEngine.Component comp ? comp.transform : null;
            if (transform == null)
            {
                return obj.name;
            }

            var sb = new System.Text.StringBuilder(transform.name);
            var t = transform.parent;
            while (t != null)
            {
                sb.Insert(0, "/");
                sb.Insert(0, t.name);
                t = t.parent;
            }
            return sb.ToString();
        }

        /// <summary>
        /// 通过反射查找所有 TextMeshProUGUI 组件，兼容不同 Unity 版本中
        /// Object.FindObjectsOfType 重载的可用情况，避免 MissingMethodException。
        /// </summary>
        /// <param name="includeInactive">是否包含挂载在非激活 GameObject 上的组件</param>
        /// <returns>找到的 TextMeshProUGUI 组件列表</returns>
        private List<TextMeshProUGUI> FindAllTMPComponents(bool includeInactive)
        {
            Type tmpType = typeof(TextMeshProUGUI);
            Type objectType = typeof(UnityEngine.Object);
            object found = null;

            // 优先尝试带 includeInactive 参数的泛型重载（Unity 5.3+）
            MethodInfo includeInactiveMethod = objectType.GetMethod(
                "FindObjectsOfType",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(bool) },
                null);

            if (includeInactiveMethod != null)
            {
                found = includeInactiveMethod
                    .MakeGenericMethod(tmpType)
                    .Invoke(null, new object[] { includeInactive });
            }

            // 回退：无参泛型重载（所有 Unity 版本都有），只覆盖激活对象
            if (found == null)
            {
                MethodInfo basicMethod = objectType.GetMethod(
                    "FindObjectsOfType",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);

                if (basicMethod != null)
                {
                    found = basicMethod
                        .MakeGenericMethod(tmpType)
                        .Invoke(null, null);
                }
            }

            var result = new List<TextMeshProUGUI>();
            if (found is object[] array)
            {
                foreach (var obj in array)
                {
                    if (obj is TextMeshProUGUI tmp)
                    {
                        result.Add(tmp);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 通过反射查找所有 UI.Text 组件，兼容不同 Unity 版本中
        /// Object.FindObjectsOfType 重载的可用情况，避免 MissingMethodException。
        /// </summary>
        /// <param name="includeInactive">是否包含挂载在非激活 GameObject 上的组件</param>
        /// <returns>找到的 UI.Text 组件列表</returns>
        private List<UnityEngine.UI.Text> FindAllTextComponents(bool includeInactive)
        {
            Type textType = typeof(UnityEngine.UI.Text);
            Type objectType = typeof(UnityEngine.Object);
            object found = null;

            // 优先尝试带 includeInactive 参数的泛型重载（Unity 5.3+）
            // 签名：T[] FindObjectsOfType<T>(bool includeInactive)
            MethodInfo includeInactiveMethod = objectType.GetMethod(
                "FindObjectsOfType",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(bool) },
                null);

            if (includeInactiveMethod != null)
            {
                found = includeInactiveMethod
                    .MakeGenericMethod(textType)
                    .Invoke(null, new object[] { includeInactive });
            }

            // 回退：使用无参泛型重载（所有 Unity 版本都有），
            // 该重载只返回激活对象，但可保证不崩溃。
            if (found == null)
            {
                MethodInfo basicMethod = objectType.GetMethod(
                    "FindObjectsOfType",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);

                if (basicMethod != null)
                {
                    found = basicMethod
                        .MakeGenericMethod(textType)
                        .Invoke(null, null);
                }
            }

            var result = new List<UnityEngine.UI.Text>();
            if (found is object[] array)
            {
                foreach (var obj in array)
                {
                    if (obj is UnityEngine.UI.Text text)
                    {
                        result.Add(text);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 从预设的字体目录中枚举所有 TTF 文件，尝试加载第一个可用的字体
        /// 并将其用作默认的 TMP 字体或动态字体。
        /// </summary>
        private void LoadDefaultFontFromDirectory()
        {
            try
            {
                // 获取所有 .ttf 或 .TTF 文件（仅在顶层目录查找）
                string[] fontFiles = Directory.GetFiles(fontsDirectory, "*.ttf", SearchOption.TopDirectoryOnly);
                if (fontFiles.Length == 0)
                {
                    fontFiles = Directory.GetFiles(fontsDirectory, "*.TTF", SearchOption.TopDirectoryOnly);
                }

                if (fontFiles.Length == 0)
                {
                    Logger.LogWarning("No TTF font files found in the Fonts directory.");
                    return;
                }

                foreach (string ttfPath in fontFiles)
                {
                    string fontName = Path.GetFileNameWithoutExtension(ttfPath);
                    var customFont = LoadTMPTTF(fontName); // 尝试加载并创建 TMP_FontAsset

                    if (customFont != null)
                    {
                        // 注意：Mono 环境下直接为属性 TMP_Settings.defaultFontAsset 赋值通常会失败
                        // （它没有公开的 set 访问器，IL2CPP 版本可绕过但 Mono 版本不一定），
                        // 因此使用反射写入其底层字段，保证跨版本兼容。
                        if (TrySetTmpDefaultFontAsset(customFont, out string setMsg))
                        {
                            Logger.LogInfo($"Successfully set default TMP font to: {fontName} ({setMsg})");
                        }
                        else
                        {
                            Logger.LogWarning(
                                $"Created TMP font {fontName} but failed to register as default ({setMsg}). " +
                                "Will rely on UI.Text dynamic font fallback.");
                        }

                        // 仅修改 TMP_Settings.defaultFontAsset 不会刷新场景中已存在的 TextMeshProUGUI 组件，
                        // 它们各自持有对旧 TMP_FontAsset 的引用。这里立刻主动遍历替换一次，
                        // 覆盖插件 Start() 阶段就能看到的 TMP 文字（例如引导/启动画面）。
                        ApplyCustomFontToAllTMPTexts();

                        return; // 成功加载一个就退出
                    }
                    else
                    {
                        Logger.LogWarning($"Failed to load font: {fontName}, trying next...");
                    }
                }

                Logger.LogError("Failed to load any font from the Fonts directory.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error loading default font from directory: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 通过反射将自定义 TMP_FontAsset 注册为 TMP_Settings.defaultFontAsset。
        /// TMP 在不同版本里要么把字段命名为 m_defaultFontAsset（旧版），
        /// 要么公开一个静态属性 defaultFontAsset，但其在 Mono 程序集里通常没有 set 访问器。
        /// 为同时兼容两种实现，这里依次尝试：静态属性 set → 私有/公有字段 m_defaultFontAsset。
        /// </summary>
        /// <param name="customFont">要注册为默认的 TMP_FontAsset</param>
        /// <param name="message">成功路径或失败原因的简短说明</param>
        /// <returns>true 表示成功写入了 defaultFontAsset；false 表示未能写入（但不会抛出异常）。</returns>
        private bool TrySetTmpDefaultFontAsset(TMP_FontAsset customFont, out string message)
        {
            if (customFont == null)
            {
                message = "customFont is null";
                return false;
            }

            Type settingsType = typeof(TMPro.TMP_Settings);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            // 1) 优先尝试静态属性 defaultFontAsset（部分版本下其 setter 是可访问的，例如 IL2CPP 运行时）
            PropertyInfo prop = settingsType.GetProperty("defaultFontAsset", flags);
            if (prop != null && prop.CanWrite)
            {
                try
                {
                    prop.SetValue(null, customFont);
                    message = "via TMP_Settings.defaultFontAsset property";
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogDebug($"[TTFLoaderMono] Property defaultFontAsset set threw: {ex.Message}");
                }
            }

            // 2) 回退到反射写底层字段。TMP 源码里常见字段名为 m_defaultFontAsset；
            //    少数自定义分支可能使用 k_DefaultFontAsset，这里都尝试一遍。
            string[] candidateFieldNames = { "m_defaultFontAsset", "k_DefaultFontAsset", "s_DefaultFontAsset" };
            foreach (string fieldName in candidateFieldNames)
            {
                FieldInfo field = settingsType.GetField(fieldName, flags);
                if (field == null)
                {
                    continue;
                }

                try
                {
                    field.SetValue(null, customFont);
                    message = $"via reflection field TMP_Settings.{fieldName}";
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(
                        $"[TTFLoaderMono] Failed to write TMP_Settings.{fieldName}: {ex.Message}");
                }
            }

            message = "no writable property or known backing field found on TMP_Settings";
            return false;
        }

        /// <summary>
        /// 标记 Font 为 dynamic 模式，让 TMP 在 Dynamic 模式下能通过 RequestCharactersInTexture
        /// 从此 Font 读取缺失字符的 glyph。Unity 把 Font.dynamic 的 setter 设为 internal，
        /// 公开 API 无法直接赋值；这里通过反射写入 m_IsDynamic 备份字段。
        /// </summary>
        private static void SetFontDynamicTrue(Font font)
        {
            if (font == null)
            {
                return;
            }
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            string[] fieldNames = { "m_IsDynamic", "m_dynamic", "isDynamic" };
            foreach (string name in fieldNames)
            {
                FieldInfo fi = typeof(Font).GetField(name, flags);
                if (fi == null || fi.FieldType != typeof(bool))
                {
                    continue;
                }
                try
                {
                    fi.SetValue(font, true);
                    return;
                }
                catch
                {
                    // 尝试下一个候选字段名
                }
            }
        }

        /// <summary>
        /// TMP_FontAsset.sourceFontFile 是只读属性，但它是 TMP 内部用于"缺失字符时读取
        /// glyph"的来源（Dynamic 模式必需）。这里通过反射强行写入备份字段，
        /// 让自定义 TMP_FontAsset 在面对未收录字符时也能正确渲染。
        /// </summary>
        private static void TrySetSourceFontFile(TMP_FontAsset asset, Font source)
        {
            if (asset == null || source == null)
            {
                return;
            }
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            string[] fieldNames = { "m_sourceFontFile", "sourceFontFile", "k_SourceFontFile" };
            foreach (string name in fieldNames)
            {
                FieldInfo fi = typeof(TMP_FontAsset).GetField(name, flags);
                if (fi == null)
                {
                    continue;
                }
                try
                {
                    fi.SetValue(asset, source);
                    return;
                }
                catch
                {
                    // 尝试下一个候选
                }
            }
            // 旧版本 TMP 用 m_fontSource；保险起见再试一次
            FieldInfo legacy = typeof(TMP_FontAsset).GetField("m_fontSource", flags);
            legacy?.SetValue(asset, source);
        }

        /// <summary>
        /// 加载 TTF 字体文件并返回 Unity Font 对象
        /// </summary>
        /// <param name="fontName">字体文件名（不含扩展名）</param>
        /// <param name="dynamic">是否创建动态字体（使用 CreateDynamicFontFromOSFont）</param>
        /// <returns>Unity Font 对象</returns>
        public Font LoadTTF(string fontName, bool dynamic = false)
        {
            Font font = null;

            // 尝试从指定的字体目录加载 .ttf 文件
            string ttfPath = Path.Combine(fontsDirectory, fontName + ".ttf");
            if (!File.Exists(ttfPath))
            {
                ttfPath = Path.Combine(fontsDirectory, fontName + ".TTF");
            }

            if (File.Exists(ttfPath))
            {
                Logger.LogInfo($"Found TTF file: {ttfPath}");
                if (dynamic)
                {
                    // 注意：CreateDynamicFontFromOSFont 只接受已安装的系统字体名（如 "Arial"），
                    // 传入文件路径会返回 null 并打印 "Unable to load font face ..." 警告。
                    // 这里传 ttfPath 时虽然走的是 dynamic 路径，但 Unity 实际无法从文件路径加载 OS 字体，
                    // 因此走 dynamic=true 时 TTF 文件路径分支会被跳过，由下面的系统字体 fallback 接管。
                    font = Font.CreateDynamicFontFromOSFont(ttfPath, 16);
                }
                else
                {
                    // 从 .ttf 文件路径直接构造 Font（Unity 会从 TTF 的 OS/2 表读取 family name）
                    font = new Font(ttfPath);
                    // 标记为动态字体（Font.dynamic 是只读属性，用反射写 m_IsDynamic 备份字段），
                    // 这样 TMP 在 Dynamic 模式下可通过 RequestCharactersInTexture 从此 Font
                    // 读取缺失字符的 glyph（即作为 TMP 的 sourceFontFile 后端）。
                    if (font != null)
                    {
                        SetFontDynamicTrue(font);
                    }
                }
            }

            if (font != null)
                return font;

            // 如果找不到本地字体文件，尝试使用系统字体
            string[] variants = {
                fontName,
                $"{fontName}-Regular",
                $"{fontName} Regular",
                "Noto Sans SC",
                "Microsoft YaHei",
                "SimHei",
                "Arial"
            };

            foreach (string variant in variants)
            {
                // 尝试从系统加载动态字体（大小设为 16）
                font = Font.CreateDynamicFontFromOSFont(variant, 16);
                if (font != null)
                {
                    font.name = variant;
                    Logger.LogInfo($"Loaded system font variant: {variant}");
                    return font;
                }
            }

            // 最终兜底方案：使用默认 Arial 字体
            Logger.LogWarning($"Using fallback font 'Arial' for: {fontName}");
            Font fallbackFonts = Font.CreateDynamicFontFromOSFont("Arial", 16);
            if (fallbackFonts != null)
            {
                fallbackFonts.name = "Arial";
                return fallbackFonts;
            }

            // 彻底失败
            return null;
        }

        /// <summary>
        /// 加载 TTF 字体并创建 TMP_FontAsset 对象。
        ///
        /// 关键点：必须把 TMP_FontAsset 的 atlasPopulationMode 设为 Dynamic，并保留 baseFont
        /// 的强引用（赋值给 fontAsset.sourceFontFile），这样当 Naninovel 在运行时注入
        /// 中文/韩文等新字符时，TMP 会主动从 sourceFontFile 读取 glyph 并动态加入 atlas，
        /// 而不是回退到原 SDF 字体显示豆腐块。
        /// </summary>
        /// <param name="fontName">字体文件名（不含扩展名）</param>
        /// <returns>TMP_FontAsset 对象</returns>
        public TMP_FontAsset LoadTMPTTF(string fontName)
        {
            try
            {
                // 先加载基础 Unity Font（dynamic=false：从 .ttf 文件路径直接加载，
                // 不要用 CreateDynamicFontFromOSFont 传文件路径——它只接受已安装的系统字体名，
                // 传文件路径会返回 null，导致 TMP_FontAsset.CreateFontAsset 后续拿到 null）。
                Font baseFont = LoadTTF(fontName, false);
                if (baseFont == null)
                {
                    Logger.LogError($"Failed to load base font: {fontName}");
                    return null;
                }

                // 创建 TMP 字体资源（baseFont 必须有效，否则返回 null）
                TMP_FontAsset tmpFont = TMP_FontAsset.CreateFontAsset(baseFont);

                if (tmpFont == null)
                {
                    Logger.LogError($"TMP_FontAsset.CreateFontAsset returned null for: {fontName}");
                    return null;
                }

                // ★ 核心：设为 Dynamic 模式，TMP 会在缺失字符时主动从 sourceFontFile 读取 glyph
                tmpFont.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                // sourceFontFile 是只读属性，用反射强行写入（TMP 内部读取它来在缺失字符时获取 glyph）
                TrySetSourceFontFile(tmpFont, baseFont);

                tmpFont.name = fontName;
                // 缓存引用，以便后续 ApplyCustomFontToAllTMPTexts 主动替换场景中已存在的 TMP 组件
                customTmpFont = tmpFont;
                Logger.LogInfo($"Successfully created TMP font: {fontName} (atlasPopulationMode=Dynamic, sourceFontFile={baseFont.name})");
                return tmpFont;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to create TMP font {fontName}: {ex.Message}\n{ex.StackTrace}");

                Logger.LogInfo("Trying use UI.Text");
                // 兜底走 UI.Text 路径：使用 dynamic 模式加载（会用 OS 字体名作为来源）
                Font baseFont = LoadTTF(fontName, true);
                if (baseFont == null)
                {
                    Logger.LogError($"Failed to load base font: {fontName}");
                    return null;
                }
                dynamicFont = baseFont;
                ApplyCustomFontToAllTexts();
                return null;
            }
        }
    }
}