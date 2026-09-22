// ============================================================================
//  tools\MakeVersionRes.cs  ——  手写 Win32 版本资源（不需要 Windows SDK / rc.exe）
//
//  【为什么需要它】
//    本机没有 Windows SDK，也就没有 rc.exe，而 csc 的 /win32res: 要的是
//    **.RES 格式**（rc.exe 的产物格式）。本工具就是"最小 rc.exe"：
//    只做一件事 —— 按 Win32 资源文件格式手写出 VERSIONINFO
//    （RtVersion=16 / VS_VERSION_INFO=1），让 exe 带上版本身份元数据。
//    二进制里没有版本信息 = "没有身份"，是杀软机器学习误报的常见诱因之一。
//
//  【格式真值来源（照抄，不是猜的）】
//    * 微软文档 "Resource File Formats"（RESOURCEHEADER / VS_VERSIONINFO /
//      StringFileInfo / StringTable / String 各字段含义与对齐规则）
//    * dotnet/roslyn  src/Compilers/Core/Portable/CvtRes.cs
//        - CvtResFile.ReadResFile()     读取侧：文件必须以"空资源"开头；
//          ID 的编码是 **首 WORD 0xFFFF + 紧跟一个 WORD 序号**
//        - AppendVersionToResourceStream() / VersionResourceSerializer
//          写入侧：逐 WORD 的字段顺序、PadKeyLen 的对齐规则、
//          文本值 wValueLength = 字符数（含结尾 NUL）、
//          二进制值 wValueLength = 字节数
//        - AppendIconToResourceStream()  图标侧：RT_ICON(3) 逐个 + RT_GROUP_ICON(14)
//          分组（ICONDIR + ICONRESDIR 数组）
//
//  【为什么连图标也一起写进 .res】
//    本机 csc **不允许 /win32icon: 与 /win32res: 同时出现**：
//        error CS1565: 指定的选项冲突: Win32 资源文件与 Win32 图标
//    而 rc.exe 的老做法本来就是"一个 .rc 里同时放 ICON 和 VERSIONINFO，
//    一次 rc 出一个 .res"。所以这里照做：--icon= 把 .ico 也编进同一个 .res，
//    build.ps1 因而不需要 /win32icon:（否则连图标都保不住）。
//
//  【版本号唯一出口】
//    只从 src\DshCore.cs 的 `public const string AppVersion = "x.y.z";` 读，
//    本工具自己**绝不另写一份版本号**；同时把生成的四段版本（如 5.0.1.0）
//    回写同步到 src\app.manifest 的 assemblyIdentity version 属性。
//
//  【编译（本机 .NET Framework csc，只支持 C# 5）】
//    csc /nologo /target:exe /codepage:65001 /out:tools\MakeVersionRes.exe tools\MakeVersionRes.cs
//
//  【用法】
//    MakeVersionRes.exe                 默认 --src=src\DshCore.cs --res=src\version.res
//                                             --manifest=src\app.manifest
//    MakeVersionRes.exe --icon=a.ico    把图标也编进同一个 .res（RT_ICON + RT_GROUP_ICON）
//    MakeVersionRes.exe --res=out\x.res 只改输出路径
//    MakeVersionRes.exe --no-manifest   不同步清单里的 version 属性
//    MakeVersionRes.exe --no-varfileinfo  不写 VarFileInfo\Translation（对照实验用）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

internal static class MakeVersionRes
{
    // ---- 常量（与 rc.exe 的默认输出保持一致）----------------------------
    private const int LangId = 0x0409;              // LANG_ENGLISH / SUBLANG_ENGLISH_US
    private const int CodePage = 1200;              // CP_WINUNICODE
    private const string LangCpKey = "040904B0";    // StringTable 键 = langid(0409) + codepage(04B0)
    private const int RtIcon = 3;                   // RT_ICON
    private const int RtGroupIcon = 14;             // RT_GROUP_ICON
    private const int IdiApplication = 0x7F00;      // 组图标 ID（与 csc /win32icon 完全一致）
    private const int RtVersion = 16;               // RT_VERSION
    private const int VsVersionInfoId = 1;          // VS_VERSION_INFO
    private const int RtManifest = 24;              // RT_MANIFEST（exe 的 Name 固定为 1）
    private const int FixedFileInfoSize = 52;       // sizeof(VS_FIXEDFILEINFO)
    private const int MemoryFlags = 0x0030;         // 版本资源的 MemoryFlags

    private const string AppVersionPattern = "public\\s+const\\s+string\\s+AppVersion\\s*=\\s*\"([^\"]+)\"";
    private const string ManifestVersionPattern = "(name=\"BigFatFishRescuer\"\\s+version=\")[^\"]*(\")";

    private static int Main(string[] args)
    {
        string srcPath = "src\\DshCore.cs";
        string resPath = "src\\version.res";
        string manifestPath = "src\\app.manifest";
        string iconPath = null;
        bool syncManifest = true;
        bool withVarFileInfo = true;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--src=", StringComparison.Ordinal)) srcPath = a.Substring("--src=".Length);
            else if (a.StartsWith("--res=", StringComparison.Ordinal)) resPath = a.Substring("--res=".Length);
            else if (a.StartsWith("--manifest=", StringComparison.Ordinal)) manifestPath = a.Substring("--manifest=".Length);
            else if (a.StartsWith("--icon=", StringComparison.Ordinal)) iconPath = a.Substring("--icon=".Length);
            else if (a == "--no-manifest") syncManifest = false;
            else if (a == "--no-varfileinfo") withVarFileInfo = false;
            else
            {
                Console.Error.WriteLine("[MakeVersionRes] FAIL: 未知参数 " + a);
                return 2;
            }
        }

        try { Console.OutputEncoding = new UTF8Encoding(false); }
        catch (Exception) { /* 某些终端不支持，忽略 */ }

        try
        {
            // ---------- 1. 从唯一出口取版本号 ----------
            if (!File.Exists(srcPath))
                throw new FileNotFoundException("找不到版本号来源文件: " + srcPath + "（当前目录 " + Directory.GetCurrentDirectory() + "）");
            string srcText = File.ReadAllText(srcPath);

            MatchCollection hits = Regex.Matches(srcText, AppVersionPattern);
            if (hits.Count != 1)
                throw new InvalidOperationException("在 " + srcPath + " 里 " + AppVersionPattern + " 命中 " + hits.Count + " 次（应当恰好 1 次）");
            string appVersion = hits[0].Groups[1].Value;
            int[] v = ParseQuad(appVersion);
            string quad = v[0] + "." + v[1] + "." + v[2] + "." + v[3];

            // ---------- 2. 同步清单里的 version（必须在写入 .res 之前做，因为清单本身也要被编进去）----------
            string manifestState;
            byte[] manifestBytes = null;
            if (!syncManifest) manifestState = "skipped (--no-manifest)";
            else if (!File.Exists(manifestPath)) manifestState = "not found, skipped: " + manifestPath;
            else
            {
                string mt = File.ReadAllText(manifestPath);
                MatchCollection mc = Regex.Matches(mt, ManifestVersionPattern);
                if (mc.Count != 1)
                    throw new InvalidOperationException("清单 " + manifestPath + " 里 assemblyIdentity version 命中 " + mc.Count + " 次（应当恰好 1 次）");
                string nt = Regex.Replace(mt, ManifestVersionPattern,
                    delegate(Match mm) { return mm.Groups[1].Value + quad + mm.Groups[2].Value; });
                if (nt == mt) manifestState = "already " + quad + " (unchanged)";
                else
                {
                    File.WriteAllText(manifestPath, nt, new UTF8Encoding(false));
                    manifestState = "version -> " + quad;
                }
                manifestBytes = File.ReadAllBytes(manifestPath);
            }

            // ---------- 3. 8 个版本字符串 ----------
            List<KeyValuePair<string, string>> strings = new List<KeyValuePair<string, string>>();
            strings.Add(new KeyValuePair<string, string>("CompanyName", "BigFatFish"));
            strings.Add(new KeyValuePair<string, string>("FileDescription", "DSH 故障救援工具（只针对 DeepSeek Harness）"));
            strings.Add(new KeyValuePair<string, string>("FileVersion", quad));
            strings.Add(new KeyValuePair<string, string>("InternalName", "BigFatFishRescuer"));
            strings.Add(new KeyValuePair<string, string>("LegalCopyright", "© 2026"));
            strings.Add(new KeyValuePair<string, string>("OriginalFilename", "大肥鱼救星.exe"));
            strings.Add(new KeyValuePair<string, string>("ProductName", "大肥鱼救星"));
            strings.Add(new KeyValuePair<string, string>("ProductVersion", quad));

            // ---------- 4. 组装 VS_VERSIONINFO ----------
            byte[][] verStrings = new byte[strings.Count][];
            for (int i = 0; i < strings.Count; i++)
            {
                string val = strings[i].Value;
                // 文本值的 wValueLength = 字符数（含结尾 NUL）—— 与 CvtRes.cs 一致
                verStrings[i] = BuildNode((ushort)(val.Length + 1), 1, strings[i].Key, UnicodeZ(val), null);
            }

            byte[] langCpTable = BuildNode(0, 1, LangCpKey, null, verStrings);
            byte[] stringFileInfo = BuildNode(0, 1, "StringFileInfo", null, new byte[][] { langCpTable });

            byte[][] rootChildren;
            if (withVarFileInfo)
            {
                byte[] transValue = new byte[4];
                transValue[0] = (byte)(LangId & 0xFF);
                transValue[1] = (byte)((LangId >> 8) & 0xFF);
                transValue[2] = (byte)(CodePage & 0xFF);
                transValue[3] = (byte)((CodePage >> 8) & 0xFF);
                // 二进制值的 wValueLength = 字节数（4）
                byte[] translation = BuildNode(4, 0, "Translation", transValue, null);
                byte[] varFileInfo = BuildNode(0, 1, "VarFileInfo", null, new byte[][] { translation });
                rootChildren = new byte[][] { varFileInfo, stringFileInfo };
            }
            else
            {
                rootChildren = new byte[][] { stringFileInfo };
            }

            byte[] versionInfo = BuildNode(FixedFileInfoSize, 0, "VS_VERSION_INFO", BuildFixedFileInfo(v), rootChildren);

            // ---------- 5. 可选：把 .ico 也编进同一个 .res ----------
            //  csc 不允许 /win32icon: 与 /win32res: 同时用（CS1565），
            //  这里按 rc.exe 的老做法把图标一起写进来，形状与 csc /win32icon 一致。
            List<IconImage> icons = null;
            if (iconPath != null && iconPath.Length > 0)
            {
                if (!File.Exists(iconPath))
                    throw new FileNotFoundException("找不到图标文件: " + iconPath);
                icons = ReadIco(iconPath);
            }

            // ---------- 6. 写出 .RES ----------
            //  条目顺序：空资源 → 图标 → 图标组 → 应用清单 → 版本信息
            //  清单也编进来是因为 csc 同样不允许 /win32manifest: 与 /win32res: 同时用
            //  （CS1564 指定的选项冲突: Win32 资源文件与 Win32 清单）。
            MemoryStream resMs = new MemoryStream();
            BinaryWriter resW = new BinaryWriter(resMs);
            WriteResHeader(resW, 0, 0, 0, 0, 0);                    // 空资源（.res 必须以此开头）
            if (icons != null)
            {
                for (int i = 0; i < icons.Count; i++)
                {
                    // MemoryFlags/LangId 取值与 CvtRes.cs 的 AppendIconToResourceStream 一致
                    WriteResHeader(resW, icons[i].Data.Length, RtIcon, i + 1, 0x1010, 0x0000);
                    resW.Write(icons[i].Data);
                    PadTo4(resMs);
                }
                byte[] group = BuildGroupIcon(icons);
                WriteResHeader(resW, group.Length, RtGroupIcon, IdiApplication, 0x1030, 0x0000);
                resW.Write(group);
                PadTo4(resMs);
            }
            if (manifestBytes != null)
            {
                // 取值与 CvtRes.cs 的 AppendManifestToResourceStream 一致（exe 的 Name = 1）
                WriteResHeader(resW, manifestBytes.Length, RtManifest, 1, 0x1030, 0x0000);
                resW.Write(manifestBytes);
                PadTo4(resMs);
            }
            WriteResHeader(resW, versionInfo.Length, RtVersion, VsVersionInfoId, MemoryFlags, LangId);
            resW.Write(versionInfo);
            PadTo4(resMs);
            resW.Flush();

            string resDir = Path.GetDirectoryName(Path.GetFullPath(resPath));
            if (resDir != null && resDir.Length > 0 && !Directory.Exists(resDir)) Directory.CreateDirectory(resDir);
            File.WriteAllBytes(resPath, resMs.ToArray());

            // ---------- 7. 打印（构建日志要能看见真的做了什么）----------
            Console.WriteLine("[MakeVersionRes] src       = " + srcPath);
            Console.WriteLine("[MakeVersionRes] AppVersion= " + appVersion + "  ->  四段 " + quad);
            Console.WriteLine("[MakeVersionRes] manifest  = " + manifestPath + "  (" + manifestState
                + (manifestBytes != null ? ", 已编入 RT_MANIFEST(24)/name=1, " + manifestBytes.Length + " bytes" : ", 未编入") + ")");
            Console.WriteLine("[MakeVersionRes] strings   = " + strings.Count);
            for (int i = 0; i < strings.Count; i++)
                Console.WriteLine("[MakeVersionRes]     " + Pad(strings[i].Key, 18) + " = " + strings[i].Value + "   (" + strings[i].Value.Length + " chars)");
            Console.WriteLine("[MakeVersionRes] res       = " + resPath + "  (" + resMs.Length + " bytes)"
                + "  [dataSize=" + versionInfo.Length + ", VarFileInfo=" + (withVarFileInfo ? "yes" : "no")
                + ", LangId=0x" + LangId.ToString("X4") + ", LangCp=" + LangCpKey + ", RT_VERSION=" + RtVersion + "/" + VsVersionInfoId + "]");
            if (icons != null)
            {
                int sum = 0;
                for (int i = 0; i < icons.Count; i++) sum += icons[i].Data.Length;
                Console.WriteLine("[MakeVersionRes] icon      = " + iconPath + "  (" + icons.Count + " 张, 共 " + sum + " 字节)"
                    + "  -> RT_ICON(3) x" + icons.Count + " + RT_GROUP_ICON(14)/id=" + IdiApplication);
            }
            Console.WriteLine("[MakeVersionRes] OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[MakeVersionRes] FAIL: " + ex.Message);
            return 1;
        }
    }

    // -------- 版本号解析：缺段补 0，最多 4 段 --------
    private static int[] ParseQuad(string appVersion)
    {
        string[] parts = appVersion.Split('.');
        int[] v = new int[4];
        for (int i = 0; i < 4; i++)
        {
            string p = (i < parts.Length) ? parts[i].Trim() : string.Empty;
            if (p.Length == 0) { v[i] = 0; continue; }
            int n;
            if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out n))
                throw new InvalidOperationException("AppVersion=\"" + appVersion + "\" 的第 " + (i + 1) + " 段 \"" + p + "\" 不是非负十进制整数");
            if (n > 65535) throw new InvalidOperationException("AppVersion 第 " + (i + 1) + " 段 " + n + " 超过 65535");
            v[i] = n;
        }
        if (parts.Length > 4)
            Console.WriteLine("[MakeVersionRes] warn: AppVersion 有 " + parts.Length + " 段，只取前 4 段");
        return v;
    }

    // -------- VS_FIXEDFILEINFO（52 字节 / 13 个 DWORD）--------
    private static byte[] BuildFixedFileInfo(int[] v)
    {
        uint ms = ((uint)v[0] << 16) | (uint)(v[1] & 0xFFFF);
        uint ls = ((uint)v[2] << 16) | (uint)(v[3] & 0xFFFF);

        MemoryStream ms64 = new MemoryStream();
        BinaryWriter w = new BinaryWriter(ms64);
        w.Write((uint)0xFEEF04BD);      // dwSignature
        w.Write((uint)0x00010000);      // dwStrucVersion
        w.Write(ms);                    // dwFileVersionMS
        w.Write(ls);                    // dwFileVersionLS
        w.Write(ms);                    // dwProductVersionMS
        w.Write(ls);                    // dwProductVersionLS
        w.Write((uint)0x3F);            // dwFileFlagsMask
        w.Write((uint)0);               // dwFileFlags
        w.Write((uint)0x00040004);      // dwFileOS = VOS_NT_WINDOWS32
        w.Write((uint)0x00000001);      // dwFileType = VFT_APP
        w.Write((uint)0);               // dwFileSubtype
        w.Write((uint)0);               // dwFileDateMS
        w.Write((uint)0);               // dwFileDateLS
        w.Flush();

        byte[] b = ms64.ToArray();
        if (b.Length != FixedFileInfoSize)
            throw new InvalidOperationException("VS_FIXEDFILEINFO 长度为 " + b.Length + "，应为 " + FixedFileInfoSize);
        return b;
    }

    // -------- 通用节点：header(3 WORD) + szKey + pad4 + Value + Children --------
    private static byte[] BuildNode(ushort valueLength, ushort type, string key, byte[] value, byte[][] children)
    {
        MemoryStream ms = new MemoryStream();
        BinaryWriter w = new BinaryWriter(ms);
        w.Write((ushort)0);             // wLength（先占位，最后回填）
        w.Write(valueLength);           // wValueLength
        w.Write(type);                  // wType
        w.Write(UnicodeZ(key));         // szKey（UTF-16 + NUL）
        PadTo4(ms);                     // 与 CvtRes.cs 的 PadKeyLen 等价

        if (value != null && value.Length > 0) w.Write(value);

        if (children != null)
        {
            for (int i = 0; i < children.Length; i++)
            {
                PadTo4(ms);             // 每个子节点必须从 4 字节边界开始
                w.Write(children[i]);
            }
        }
        w.Flush();

        byte[] buf = ms.ToArray();
        if (buf.Length > 0xFFFF)
            throw new InvalidOperationException("节点 " + key + " 长度 " + buf.Length + " 超过 WORD 上限 65535");
        buf[0] = (byte)(buf.Length & 0xFF);
        buf[1] = (byte)((buf.Length >> 8) & 0xFF);
        return buf;
    }

    // -------- .RES 资源头（32 字节；与 CvtRes.cs 的 RESOURCEHEADER 一致）--------
    private static void WriteResHeader(BinaryWriter w, int dataSize, int typeOrdinal, int nameOrdinal, int memoryFlags, int langId)
    {
        w.Write((uint)dataSize);            // DataSize（不含头）
        w.Write((uint)0x20);                // HeaderSize = 32
        w.Write((ushort)0xFFFF);            // Type：首 WORD 0xFFFF ⇒ 序号形式
        w.Write((ushort)typeOrdinal);       // Type 序号（RT_VERSION=16 / RT_ICON=3 / RT_GROUP_ICON=14）
        w.Write((ushort)0xFFFF);            // Name：同上
        w.Write((ushort)nameOrdinal);       // Name 序号
        w.Write((uint)0);                   // DataVersion
        w.Write((ushort)memoryFlags);       // MemoryFlags
        w.Write((ushort)langId);            // LanguageId
        w.Write((uint)0);                   // Version
        w.Write((uint)0);                   // Characteristics
    }

    // -------- .ico 解析（只取每张图的原始字节 + 目录字段）--------
    internal sealed class IconImage
    {
        internal byte Width;
        internal byte Height;
        internal byte ColorCount;
        internal byte Reserved;
        internal ushort Planes;
        internal ushort BitCount;
        internal byte[] Data;
    }

    private static List<IconImage> ReadIco(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        if (raw.Length < 6) throw new InvalidOperationException("图标文件太小，不是合法 .ico: " + path);
        int reserved = raw[0] | (raw[1] << 8);
        int type = raw[2] | (raw[3] << 8);
        int count = raw[4] | (raw[5] << 8);
        if (reserved != 0) throw new InvalidOperationException("图标 idReserved != 0，不是合法 .ico: " + path);
        if (type != 1) throw new InvalidOperationException("图标 idType != 1（不是图标），不是合法 .ico: " + path);
        if (count <= 0) throw new InvalidOperationException("图标里没有任何图像（idCount=0）: " + path);
        if (raw.Length < 6 + 16 * count) throw new InvalidOperationException("图标目录被截断: " + path);

        List<IconImage> list = new List<IconImage>(count);
        for (int i = 0; i < count; i++)
        {
            int off = 6 + 16 * i;
            IconImage img = new IconImage();
            img.Width = raw[off];
            img.Height = raw[off + 1];
            img.ColorCount = raw[off + 2];
            img.Reserved = raw[off + 3];
            img.Planes = (ushort)(raw[off + 4] | (raw[off + 5] << 8));
            img.BitCount = (ushort)(raw[off + 6] | (raw[off + 7] << 8));
            long cb = (uint)(raw[off + 8] | (raw[off + 9] << 8) | (raw[off + 10] << 16) | (raw[off + 11] << 24));
            long imgOff = (uint)(raw[off + 12] | (raw[off + 13] << 8) | (raw[off + 14] << 16) | (raw[off + 15] << 24));
            if (cb <= 0 || imgOff <= 0 || imgOff + cb > raw.Length)
                throw new InvalidOperationException("图标第 " + (i + 1) + " 张图像范围越界（cb=" + cb + ", off=" + imgOff + ", file=" + raw.Length + "）: " + path);
            img.Data = new byte[cb];
            Buffer.BlockCopy(raw, (int)imgOff, img.Data, 0, (int)cb);
            list.Add(img);
        }
        return list;
    }

    // -------- RT_GROUP_ICON 数据 = ICONDIR + ICONRESDIR[count] --------
    private static byte[] BuildGroupIcon(List<IconImage> icons)
    {
        MemoryStream ms = new MemoryStream();
        BinaryWriter w = new BinaryWriter(ms);
        w.Write((ushort)0);                     // idReserved
        w.Write((ushort)1);                     // idType = 1（图标）
        w.Write((ushort)icons.Count);           // idCount
        for (int i = 0; i < icons.Count; i++)
        {
            w.Write(icons[i].Width);
            w.Write(icons[i].Height);
            w.Write(icons[i].ColorCount);
            w.Write(icons[i].Reserved);
            w.Write(icons[i].Planes);
            w.Write(icons[i].BitCount);
            w.Write((uint)icons[i].Data.Length); // dwBytesInRes
            w.Write((ushort)(i + 1));            // 对应 RT_ICON 的序号
        }
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] UnicodeZ(string s)
    {
        byte[] body = Encoding.Unicode.GetBytes(s);
        byte[] r = new byte[body.Length + 2];
        Buffer.BlockCopy(body, 0, r, 0, body.Length);
        return r;
    }

    private static void PadTo4(Stream s)
    {
        while ((s.Length % 4) != 0) s.WriteByte(0);
    }

    private static string Pad(string s, int n)
    {
        while (s.Length < n) s = s + " ";
        return s;
    }
}
