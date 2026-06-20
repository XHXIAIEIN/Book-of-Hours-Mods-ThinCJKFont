using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using TMPro;
using Debug = UnityEngine.Debug;

// 入口类: 名字必须与 mod 名(去非字母数字)一致, 且在全局命名空间.
public static class ThinCJKFont
{
    public static void Initialise()
    {
        try
        {
            var go = new GameObject("ThinCJKFontLoader");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<ThinCJKFontLoader>();
            Debug.Log("[ThinCJKFont] loader spawned");
        }
        catch (Exception e) { Debug.LogError("[ThinCJKFont] Initialise failed: " + e); }
    }
}

// 用户可调参数; 字段名与 config.json 的键一一对应。
// 语义: config.json 里写了某键就按字面值生效, 不写(或删掉)该键则保持游戏原值。
// 因此没有"禁用哨兵", 每个数值都可自由取负——含义不会和"保持原值"撞车。
[Serializable]
public class ThinCJKConfig
{
    public int    configVersion     = 1;
    public bool   enabled           = true;

    public float  normalWeight      = 0.25f;
    public float  boldWeight        = 0.85f;
    public float  thickness         = 0f;

    public bool   isDisableItalic   = true;
    public string italicColor       = "#FFE300";

    public float  glyphWidthScale   = 1f;
    public float  glyphHeightScale  = 1f;
    public float  normalSpacing     = 0f;
    public float  boldSpacing       = 0f;
    public float  normalStyleWeight = 0f;
    public float  boldStyleWeight   = 0f;
    public float  outlineWidth      = 0f;
    public float  outlineSoftness   = 0f;
    public string outlineColor      = "";
}

// 不替换字形、不引入外部字体: 只调整游戏内置 CJK 字体(思源黑体 / NotoSansCJKsc)的显示。
// 数值来自 mod 目录下的 config.json, 编辑保存后自动热重载, 无需重启。
public class ThinCJKFontLoader : MonoBehaviour
{
    private const int CurrentConfigVersion = 2; // 新增字段时 +1, 触发已有 config.json 的增量迁移

    private ThinCJKConfig _cfg = new ThinCJKConfig();
    // config.json 顶层实际出现的键; 决定哪些字段生效(缺失=保持游戏原值)。
    private readonly HashSet<string> _present = new HashSet<string>(StringComparer.Ordinal);
    private string _configPath;
    private long _lastWrite = -1;
    private float _timer;

    private Color32 _italicColor32 = new Color32(0xFF, 0xE3, 0x00, 0xFF);
    private bool _recolorItalic = true; // italicColor 非空且可解析时才上色
    private bool _scaleW, _scaleH;      // glyph 横/纵缩放是否生效(预计算, 供 OnTextChanged 一次早退)

    private readonly HashSet<TMP_FontAsset> _cjkFonts = new HashSet<TMP_FontAsset>();
    private readonly Dictionary<TMP_FontAsset, FontOrig> _origFont = new Dictionary<TMP_FontAsset, FontOrig>();
    private readonly Dictionary<Material, MatOrig> _origMat = new Dictionary<Material, MatOrig>();
    private readonly HashSet<Material> _loggedMat = new HashSet<Material>();

    private struct FontOrig { public byte italic; public float nSpace, bSpace, nStyle, bStyle; }
    private struct MatOrig { public float wN, wB, dil, outW, outS; public Color outCol; }

    // =================== 生命周期 ===================

    private void Start()
    {
        _configPath = ResolveConfigPath();
        Debug.Log("[ThinCJKFont] config path = " + _configPath);
        LoadConfig(createIfMissing: true);
        WriteHelpFile(); // 每次启动刷新说明文件, 始终匹配当前 mod 版本的字段
    }

    private void OnEnable()
    {
        try { TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnTextChanged); }
        catch (Exception e) { Debug.LogError("[ThinCJKFont] 订阅 TEXT_CHANGED 失败: " + e); }
    }

    private void OnDisable()
    {
        try { TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(OnTextChanged); }
        catch { }
    }

    private void Update()
    {
        _timer += Time.unscaledDeltaTime;
        if (_timer < 0.5f) return;
        _timer = 0f;
        try
        {
            if (ConfigChangedOnDisk()) { LoadConfig(false); RefreshBabelfish(); RefreshAllTextMeshes(); } // 热重载
            Apply();
        }
        catch (Exception e) { Debug.LogError("[ThinCJKFont] update error: " + e); }
    }

    // =================== 配置路径 ===================

    // config.json / config.help.html / synopsis.json 并排放在 mod 根目录, 游戏内「浏览文件」即可找到。
    // 通过本 DLL 自身位置定位(.../ThinCJKFont/dll/ThinCJKFont.dll -> 上溯到 ThinCJKFont/);
    // 万一拿不到位置, 退回本地安装路径 mods/ThinCJKFont。
    private static string ResolveConfigPath()
    {
        string modRoot = null;
        try
        {
            string dll = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(dll))
                dll = new Uri(Assembly.GetExecutingAssembly().CodeBase).LocalPath;
            if (!string.IsNullOrEmpty(dll) && File.Exists(dll))
            {
                string dir = Path.GetDirectoryName(dll);
                if (string.Equals(Path.GetFileName(dir), "dll", StringComparison.OrdinalIgnoreCase))
                    dir = Path.GetDirectoryName(dir); // 跳出 /dll 子目录
                modRoot = dir;
            }
        }
        catch { }

        if (string.IsNullOrEmpty(modRoot) || !Directory.Exists(modRoot))
        {
            try { modRoot = Path.Combine(Application.persistentDataPath, "mods", "ThinCJKFont"); }
            catch { modRoot = "."; }
        }
        return Path.Combine(modRoot, "config.json");
    }

    // =================== 配置读写 / 增量迁移 ===================

    private bool ConfigChangedOnDisk()
    {
        try
        {
            if (!File.Exists(_configPath)) return false;
            long t = File.GetLastWriteTimeUtc(_configPath).Ticks;
            if (t != _lastWrite) { _lastWrite = t; return true; }
        }
        catch { }
        return false;
    }

    private void LoadConfig(bool createIfMissing)
    {
        try
        {
            // 不存在 -> 自动创建一份默认(只含常用键); 之后照常读回以填充 _present。
            if (!File.Exists(_configPath))
            {
                if (!createIfMissing) { _present.Clear(); RecomputeVertexWork(); return; }
                _present.Clear();
                WriteConfig(new ThinCJKConfig { configVersion = CurrentConfigVersion });
            }

            string json = StripJsonComments(File.ReadAllText(_configPath, Encoding.UTF8));

            _present.Clear();
            foreach (var k in CollectTopLevelKeys(json)) _present.Add(k);

            var cfg = new ThinCJKConfig();
            JsonUtility.FromJsonOverwrite(json, cfg); // 缺失键保留默认值
            int fileVersion = _present.Contains("configVersion") ? cfg.configVersion : 0;
            _cfg = cfg;
            RecomputeVertexWork();

            if (fileVersion != CurrentConfigVersion)
            {
                MigrateConfig(fileVersion);   // 清洗旧哨兵 / 补齐, 全部反映到 _present
                _cfg.configVersion = CurrentConfigVersion;
                _present.Add("configVersion");
                WriteConfig(_cfg);            // 回写: 常用键 + 用户保留的进阶键(见 _present)
                Debug.Log("[ThinCJKFont] 配置已从 v" + fileVersion + " 迁移到 v" + CurrentConfigVersion + " (保留原有设置)");
            }
            else
            {
                try { _lastWrite = File.GetLastWriteTimeUtc(_configPath).Ticks; } catch { }
            }

            Debug.Log("[ThinCJKFont] config: enabled=" + _cfg.enabled + " present=[" + string.Join(",", _present) + "]");
        }
        catch (Exception e) { Debug.LogError("[ThinCJKFont] 读取配置失败, 沿用当前值: " + e); }
    }

    // 版本迁移。v2 起改用"字段存在性"语义: 旧文件里等于旧哨兵的进阶字段(缩放=1 / 其余=-1)
    // 当年表示"保持原值", 现在等价于"不写该键", 故从 _present 移除, 回写时自然消失。
    private void MigrateConfig(int fileVersion)
    {
        if (fileVersion < 2)
        {
            DropSentinel("glyphHeightScale", 1f);
            DropSentinel("normalSpacing", -1f);
            DropSentinel("boldSpacing", -1f);
            DropSentinel("normalStyleWeight", -1f);
            DropSentinel("boldStyleWeight", -1f);
            DropSentinel("outlineWidth", -1f);
            DropSentinel("outlineSoftness", -1f);
        }
    }

    private void DropSentinel(string key, float sentinel)
    {
        if (!_present.Contains(key)) return;
        var fi = typeof(ThinCJKConfig).GetField(key);
        if (fi != null && fi.GetValue(_cfg) is float v && Mathf.Approximately(v, sentinel))
            _present.Remove(key);
    }

    // 预计算"每次文本重建是否需要动顶点"的开关, 让 OnTextChanged 用一次字段读就能早退,
    // 不必每个回调都查 HashSet / 算 Approximately。只在 LoadConfig(启动 + 热重载)时跑一次。
    // 刷新时机: 颜色/缩放改完后由 RefreshAllTextMeshes 当帧重建一次即可见。
    private void RecomputeVertexWork()
    {
        _recolorItalic = TryParseColor(_cfg.italicColor, out var c);
        if (_recolorItalic) _italicColor32 = (Color32)c;
        _scaleW = _present.Contains("glyphWidthScale") && !Mathf.Approximately(_cfg.glyphWidthScale, 1f);
        _scaleH = _present.Contains("glyphHeightScale") && !Mathf.Approximately(_cfg.glyphHeightScale, 1f);
    }

    // 颜色字符串解析: 先按原样(兼容颜色名如 "red"), 失败再自动补 "#" 重试(兼容 "FFE300")。留空=false。
    private static bool TryParseColor(string s, out Color c)
    {
        c = Color.white;
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        return ColorUtility.TryParseHtmlString(s, out c)
            || (s[0] != '#' && ColorUtility.TryParseHtmlString("#" + s, out c));
    }

    private void WriteConfig(ThinCJKConfig cfg)
    {
        try
        {
            File.WriteAllText(_configPath, BuildConfigText(cfg, _present), new UTF8Encoding(false));
            _lastWrite = File.GetLastWriteTimeUtc(_configPath).Ticks;
            Debug.Log("[ThinCJKFont] 已写入配置: " + _configPath);
        }
        catch (Exception e) { Debug.LogError("[ThinCJKFont] 写配置失败: " + e); }
    }

    // 把字段说明写到独立的 config.help.html(与 config.json 并排)。
    // config.json 保持纯 JSON、零注释; 说明全部在这里, 升级 mod 后会随 Fields 自动更新。
    private void WriteHelpFile()
    {
        try
        {
            string dir = Path.GetDirectoryName(_configPath);
            if (string.IsNullOrEmpty(dir)) return;
            string path = Path.Combine(dir, "config.help.html");
            File.WriteAllText(path, BuildHelpHtml(), new UTF8Encoding(false));
            Debug.Log("[ThinCJKFont] 已写入说明: " + path);
        }
        catch (Exception e) { Debug.LogError("[ThinCJKFont] 写说明失败: " + e); }
    }

    private static string F(float v) { return v.ToString("0.######", CultureInfo.InvariantCulture); }
    private static string B(bool v) { return v ? "true" : "false"; }

    // 字段说明表: 单一数据源, 同时驱动 config.json 的分组排版与 config.help.html 的生成。
    // header 非空 => 该字段前另起一个分组。optional => 不写进默认 config.json, 只在说明里列出。
    private struct FieldDoc { public string key, header, desc, def; public bool optional; }
    private static readonly FieldDoc[] Fields =
    {
        new FieldDoc { key = "configVersion",     desc = "由 mod 维护, 请勿手改", def = "1" },
        new FieldDoc { key = "enabled",           desc = "总开关, false 恢复原版", def = "true" },

        new FieldDoc { key = "normalWeight",      header = "字重", desc = "正文字重, 越大越粗、越小越细", def = "0.25" },
        new FieldDoc { key = "boldWeight",        desc = "粗体字重", def = "0.85" },
        new FieldDoc { key = "thickness",         desc = "整体加粗", def = "0" },

        new FieldDoc { key = "isDisableItalic",   header = "斜体", desc = "取消斜体倾斜", def = "true" },
        new FieldDoc { key = "italicColor",       desc = "斜体颜色, 留空则不改色", def = "#FFE300" },

        new FieldDoc { key = "glyphWidthScale",   header = "字形缩放", desc = "横向比例, 小于 1 变窄", def = "1" },
        new FieldDoc { key = "glyphHeightScale",  desc = "纵向比例", optional = true },

        new FieldDoc { key = "normalSpacing",     header = "进阶 (按需添加; 删掉该键即恢复原样)", desc = "正文字距", optional = true },
        new FieldDoc { key = "boldSpacing",       desc = "粗体字距", optional = true },
        new FieldDoc { key = "normalStyleWeight", desc = "正文合成字重", optional = true },
        new FieldDoc { key = "boldStyleWeight",   desc = "粗体合成字重", optional = true },
        new FieldDoc { key = "outlineWidth",      desc = "描边宽度", optional = true },
        new FieldDoc { key = "outlineSoftness",   desc = "描边柔化", optional = true },
        new FieldDoc { key = "outlineColor",      desc = "描边颜色", optional = true },
    };

    private static string RenderJsonValue(object v)
    {
        if (v is bool b)   return B(b);
        if (v is int i)    return i.ToString(CultureInfo.InvariantCulture);
        if (v is float f)  return F(f);
        if (v is string s) return "\"" + EscapeJsonString(s) + "\"";
        return "null";
    }

    private static string EscapeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:   sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    // 渲染纯 JSON 的 config.json: 不含任何注释/尾随逗号, 任何工具都能当合法 JSON 打开。
    // 写出常用键, 外加 include 里出现的进阶键(迁移时用它保留用户已加的进阶项)。
    // 说明文字一律不进 config.json, 改由同目录的 config.help.html 承载。
    private static string BuildConfigText(ThinCJKConfig c, HashSet<string> include)
    {
        var t = typeof(ThinCJKConfig);
        var rows = new List<FieldDoc>();
        foreach (var fd in Fields)
            if (!fd.optional || (include != null && include.Contains(fd.key))) rows.Add(fd);

        var sb = new StringBuilder();
        sb.Append("{\n");
        for (int i = 0; i < rows.Count; i++)
        {
            var fd = rows[i];
            var fi = t.GetField(fd.key);
            if (fi == null) continue;
            if (i > 0 && !string.IsNullOrEmpty(fd.header)) sb.Append("\n"); // 分组空行
            sb.Append("  \"").Append(fd.key).Append("\": ").Append(RenderJsonValue(fi.GetValue(c)));
            sb.Append(i < rows.Count - 1 ? ",\n" : "\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    // 渲染 config.help.html: 字段说明的唯一去处。与 config.json 共用 Fields, 不会两边对不上。
    private static string BuildHelpHtml()
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"zh-CN\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>ThinCJKFont 配置说明</title>\n<style>\n");
        sb.Append("body{max-width:760px;margin:40px auto;padding:0 20px;font-family:'Segoe UI','Microsoft YaHei',sans-serif;line-height:1.7;color:#3a3128;background:#f4ecdd;}\n");
        sb.Append("h1{font-size:1.5em;color:#685D4F;border-bottom:2px solid #cdbfa3;padding-bottom:.3em;}\n");
        sb.Append("p.intro{color:#5a4f3f;}\n");
        sb.Append("code{background:#e8ddc6;padding:.1em .4em;border-radius:3px;font-family:Consolas,monospace;}\n");
        sb.Append("table{border-collapse:collapse;width:100%;margin-top:1em;}\n");
        sb.Append("th,td{text-align:left;padding:.5em .7em;border-bottom:1px solid #ddd0b5;vertical-align:top;}\n");
        sb.Append("th{background:#e8ddc6;color:#685D4F;}\n");
        sb.Append("tr.group td{background:#efe5d0;font-weight:bold;color:#7a6a52;}\n");
        sb.Append("td.key{font-family:Consolas,monospace;white-space:nowrap;color:#4a3f30;}\n");
        sb.Append("td.def{font-family:Consolas,monospace;color:#8a7a5e;white-space:nowrap;}\n");
        sb.Append("</style>\n</head>\n<body>\n");
        sb.Append("<h1>ThinCJKFont 配置说明</h1>\n");
        sb.Append("<p class=\"intro\">编辑同目录的 <code>config.json</code>，保存即时生效，无需重启。");
        sb.Append("下表每个键都能写进 <code>config.json</code>；不写或删掉某个键，该项就保持游戏原样。");
        sb.Append("把 <code>enabled</code> 改成 <code>false</code> 可整体恢复原版。</p>\n");
        sb.Append("<table>\n<tr><th>键</th><th>默认</th><th>说明</th></tr>\n");
        foreach (var fd in Fields)
        {
            if (!string.IsNullOrEmpty(fd.header))
                sb.Append("<tr class=\"group\"><td colspan=\"3\">").Append(HtmlEscape(fd.header)).Append("</td></tr>\n");
            string def = fd.optional ? "—" : HtmlEscape(fd.def);
            sb.Append("<tr><td class=\"key\">").Append(HtmlEscape(fd.key)).Append("</td>");
            sb.Append("<td class=\"def\">").Append(def).Append("</td>");
            sb.Append("<td>").Append(HtmlEscape(fd.desc)).Append("</td></tr>\n");
        }
        sb.Append("</table>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static string HtmlEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    // 取出 JSON 顶层(深度 1)实际出现的键名。JsonUtility 无法区分"键缺失"与"键=默认值",
    // 所以靠这个判断哪些字段该生效。只看顶层, 跳过字符串内部, 不依赖字段扁平假设。
    private static HashSet<string> CollectTopLevelKeys(string json)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        int depth = 0;
        bool inStr = false, esc = false, expectKey = false, capturing = false;
        var cur = new StringBuilder();
        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];
            if (inStr)
            {
                if (esc) { esc = false; if (capturing) cur.Append(ch); }
                else if (ch == '\\') esc = true;
                else if (ch == '"') { inStr = false; if (capturing) { keys.Add(cur.ToString()); capturing = false; } }
                else if (capturing) cur.Append(ch);
                continue;
            }
            if (ch == '"')
            {
                inStr = true;
                if (depth == 1 && expectKey) { capturing = true; expectKey = false; cur.Length = 0; }
                continue;
            }
            switch (ch)
            {
                case '{': depth++; if (depth == 1) expectKey = true; break;
                case '}': depth--; break;
                case '[': depth++; break;
                case ']': depth--; break;
                case ',': if (depth == 1) expectKey = true; break;
            }
        }
        return keys;
    }

    // 容错读取: config.json 现在写出的是纯 JSON, 但旧版本曾写入带 // 注释的文件,
    // 这里仍把 // 行注释 / 块注释(跳过字符串内部)和尾随逗号去掉, 兼容老配置不报错。
    private static string StripJsonComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool inStr = false, esc = false;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (inStr)
            {
                sb.Append(ch);
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') { inStr = true; sb.Append(ch); continue; }
            if (ch == '/' && i + 1 < s.Length && s[i + 1] == '/')
            { i += 2; while (i < s.Length && s[i] != '\n') i++; if (i < s.Length) sb.Append('\n'); continue; }
            if (ch == '/' && i + 1 < s.Length && s[i + 1] == '*')
            { i += 2; while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++; i += 1; continue; }
            sb.Append(ch);
        }
        return Regex.Replace(sb.ToString(), @",(\s*[}\]])", "$1");
    }

    // =================== 顶点层: 网格横纵比例 + 斜体上色(不动文本源串) ===================

    private void OnTextChanged(UnityEngine.Object obj)
    {
        try
        {
            if (!_cfg.enabled || _cjkFonts.Count == 0) return;
            if (!_scaleW && !_scaleH && !_recolorItalic) return;

            var tmp = obj as TMP_Text;
            if (tmp == null) return;
            var ti = tmp.textInfo;
            if (ti == null) return;

            // 颜色仅作用于斜体: 文本无 <i> 时跳过上色(但缩放仍要做)
            string src = tmp.text;
            bool colorThisText = _recolorItalic && !string.IsNullOrEmpty(src) &&
                                 src.IndexOf("<i", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!_scaleW && !_scaleH && !colorThisText) return;

            bool vchanged = false, cchanged = false;
            int n = ti.characterCount;
            for (int i = 0; i < n; i++)
            {
                var ci = ti.characterInfo[i];
                if (!ci.isVisible) continue;
                if (!_cjkFonts.Contains(ci.fontAsset)) continue;

                int mi = ci.materialReferenceIndex;
                if (mi < 0 || mi >= ti.meshInfo.Length) continue;
                int vi = ci.vertexIndex;

                // 网格缩放(所有 CJK 字): TMP 顶点序 0=BL 1=TL 2=TR 3=BR
                if (_scaleW || _scaleH)
                {
                    var verts = ti.meshInfo[mi].vertices;
                    if (verts != null && vi >= 0 && vi + 3 < verts.Length)
                    {
                        float cx = (verts[vi].x + verts[vi + 2].x) * 0.5f; // 绕字形横向中心
                        float by = ci.baseLine;                            // 绕基线缩放纵向, 不破坏行对齐
                        for (int k = 0; k < 4; k++)
                        {
                            var v = verts[vi + k];
                            if (_scaleW) v.x = cx + (v.x - cx) * _cfg.glyphWidthScale;
                            if (_scaleH) v.y = by + (v.y - by) * _cfg.glyphHeightScale;
                            verts[vi + k] = v;
                        }
                        vchanged = true;
                    }
                }

                // 斜体上色(仅斜体 CJK 字)
                if (colorThisText && (ci.style & FontStyles.Italic) == FontStyles.Italic)
                {
                    var cols = ti.meshInfo[mi].colors32;
                    if (cols != null && vi >= 0 && vi + 3 < cols.Length)
                    {
                        cols[vi] = cols[vi + 1] = cols[vi + 2] = cols[vi + 3] = _italicColor32;
                        cchanged = true;
                    }
                }
            }

            var flags = TMP_VertexDataUpdateFlags.None;
            if (vchanged) flags |= TMP_VertexDataUpdateFlags.Vertices;
            if (cchanged) flags |= TMP_VertexDataUpdateFlags.Colors32;
            if (flags != TMP_VertexDataUpdateFlags.None) tmp.UpdateVertexData(flags);
        }
        catch (Exception e) { Debug.LogError("[ThinCJKFont] OnTextChanged 失败: " + e); }
    }

    // =================== 字体/材质层: 字重、去倾斜、字距、描边 ===================

    private void Apply()
    {
        var lm = UnityEngine.Object.FindObjectOfType<LanguageManager>();
        if (lm == null || lm.fontStyles == null || lm.fontStyles.Length == 0) return;

        bool changed = false;
        foreach (var fs in lm.fontStyles)
        {
            if (fs == null || fs.fontCJK == null) continue;
            var font = fs.fontCJK;
            _cjkFonts.Add(font);
            var of = CaptureFont(font);

            byte targetItalic = (_cfg.enabled && _present.Contains("isDisableItalic") && _cfg.isDisableItalic) ? (byte)0 : of.italic;
            if (font.italicStyle != targetItalic) { font.italicStyle = targetItalic; changed = true; }

            changed |= SetFontFloat(ref font.normalSpacingOffset, Pick("normalSpacing", _cfg.normalSpacing, of.nSpace), font);
            changed |= SetFontFloat(ref font.boldSpacing,         Pick("boldSpacing", _cfg.boldSpacing, of.bSpace), font);
            changed |= SetFontFloat(ref font.normalStyle,         Pick("normalStyleWeight", _cfg.normalStyleWeight, of.nStyle), font);
            changed |= SetFontFloat(ref font.boldStyle,           Pick("boldStyleWeight", _cfg.boldStyleWeight, of.bStyle), font);

            var mat = font.material;
            if (mat != null && ApplyToMaterial(mat)) changed = true;
        }

        if (changed) RefreshBabelfish();
    }

    // enabled 且 config.json 写了该键 -> 用配置值; 否则(关闭, 或没写该键)保持游戏原值。
    private float Pick(string key, float cfgVal, float orig)
    {
        return (_cfg.enabled && _present.Contains(key)) ? cfgVal : orig;
    }

    private static bool SetFontFloat(ref float field, float target, TMP_FontAsset _)
    {
        if (Mathf.Approximately(field, target)) return false;
        field = target;
        return true;
    }

    private FontOrig CaptureFont(TMP_FontAsset font)
    {
        if (_origFont.TryGetValue(font, out var of)) return of;
        of = new FontOrig
        {
            italic = font.italicStyle,
            nSpace = font.normalSpacingOffset,
            bSpace = font.boldSpacing,
            nStyle = font.normalStyle,
            bStyle = font.boldStyle,
        };
        _origFont[font] = of;
        // 打印原始度量, 方便在 player.log 里核对游戏到底把字距/比例设成了多少
        string face = "?";
        try { var fi = font.faceInfo; face = "scale=" + fi.scale + " pointSize=" + fi.pointSize + " lineHeight=" + fi.lineHeight; }
        catch { }
        Debug.Log("[ThinCJKFont] 字体 '" + font.name + "' 原始: italicStyle=" + of.italic +
                  " normalSpacing=" + of.nSpace + " boldSpacing=" + of.bSpace +
                  " normalStyle=" + of.nStyle + " boldStyle=" + of.bStyle + " | FaceInfo " + face);
        return of;
    }

    private bool ApplyToMaterial(Material mat)
    {
        if (!_origMat.TryGetValue(mat, out var o))
        {
            o = new MatOrig
            {
                wN = Get(mat, "_WeightNormal"),
                wB = Get(mat, "_WeightBold"),
                dil = Get(mat, "_FaceDilate"),
                outW = Get(mat, "_OutlineWidth"),
                outS = Get(mat, "_OutlineSoftness"),
                outCol = mat.HasProperty("_OutlineColor") ? mat.GetColor("_OutlineColor") : Color.black,
            };
            _origMat[mat] = o;
            if (_loggedMat.Add(mat))
                Debug.Log("[ThinCJKFont] 材质 '" + mat.name + "' 原始: _WeightNormal=" + o.wN + " _WeightBold=" + o.wB +
                          " _FaceDilate=" + o.dil + " _OutlineWidth=" + o.outW + " _OutlineSoftness=" + o.outS +
                          " _OutlineColor=" + o.outCol);
        }

        float tN = Pick("normalWeight", _cfg.normalWeight, o.wN);
        float tB = Pick("boldWeight", _cfg.boldWeight, o.wB);
        float tD = Pick("thickness", _cfg.thickness, o.dil);
        float tOW = Pick("outlineWidth", _cfg.outlineWidth, o.outW);
        float tOS = Pick("outlineSoftness", _cfg.outlineSoftness, o.outS);
        // 描边颜色走字符串解析(同 italicColor 容错); 写了才改, 否则还原原色。属于材质参数, 改了即时生效。
        Color tOC = (_cfg.enabled && _present.Contains("outlineColor") && TryParseColor(_cfg.outlineColor, out var oc)) ? oc : o.outCol;

        bool changed = false;
        changed |= SetIfNeeded(mat, "_WeightNormal", tN);
        changed |= SetIfNeeded(mat, "_WeightBold", tB);
        changed |= SetIfNeeded(mat, "_FaceDilate", tD);
        changed |= SetIfNeeded(mat, "_OutlineWidth", tOW);
        changed |= SetIfNeeded(mat, "_OutlineSoftness", tOS);
        changed |= SetColorIfNeeded(mat, "_OutlineColor", tOC);
        return changed;
    }

    private static bool SetIfNeeded(Material m, string prop, float target)
    {
        if (!m.HasProperty(prop)) return false;
        if (Mathf.Approximately(m.GetFloat(prop), target)) return false;
        m.SetFloat(prop, target);
        return true;
    }

    private static bool SetColorIfNeeded(Material m, string prop, Color target)
    {
        if (!m.HasProperty(prop)) return false;
        if (m.GetColor(prop) == target) return false; // Unity Color == 为分量近似比较
        m.SetColor(prop, target);
        return true;
    }

    private static float Get(Material m, string prop)
    {
        return m.HasProperty(prop) ? m.GetFloat(prop) : 0f;
    }

    // 强制重建所有已显示文本的网格 -> 触发 OnTextChanged 重新上色/缩放。
    // 颜色与顶点缩放只在网格生成那刻写入(不像材质字重那样即时), 故热重载后须主动重建一次;
    // 真重建还能把"颜色改空=取消上色"正确清回原色。
    private void RefreshAllTextMeshes()
    {
        foreach (var tmp in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            try
            {
                if (tmp == null || !tmp.isActiveAndEnabled || !tmp.gameObject.scene.IsValid()) continue; // 跳过 prefab/未激活
                tmp.ForceMeshUpdate();
            }
            catch { }
        }
    }

    // 让已存在的文本立刻按新字体/材质重排(同时触发顶点缩放/上色回调)
    private void RefreshBabelfish()
    {
        int refreshed = 0;
        foreach (var b in Resources.FindObjectsOfTypeAll<Babelfish>())
        {
            try
            {
                if (b == null || !b.gameObject.scene.IsValid()) continue;
                b.SetValuesForCurrentCulture();
                refreshed++;
            }
            catch { }
        }
        Debug.Log("[ThinCJKFont] 已应用; refreshed " + refreshed + " Babelfish");
    }
}
